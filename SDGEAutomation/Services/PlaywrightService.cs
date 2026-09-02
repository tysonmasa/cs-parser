using Microsoft.Playwright;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SDGEAutomation.Services;

public class InvalidCredentialsException : Exception
{
    public InvalidCredentialsException()
        : base("Invalid credentials")
    {
    }
}

public class NoBillAvailableException : Exception
{
    public NoBillAvailableException(string message) : base(message)
    {
    }
}

public class PlaywrightService
{
    private const int LoginCompletionTimeoutMs = 60_000;
    private static readonly IReadOnlyDictionary<string, string> GrossmontStreetAbbreviations =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["B"] = "BUCKLAND ST",
            ["P"] = "PARKWAY DR"
        };

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;

    public async Task LaunchAsync()
    {
        await CloseAsync();

        _playwright = await Playwright.CreateAsync();

        _browser = await _playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions
            {
                Headless = false
            });

        _page = await _browser.NewPageAsync();
    }

    public async Task GoToLoginPageAsync()
    {
        if (_page == null)
            throw new Exception("Browser has not been launched.");

        await _page.GotoAsync("https://myenergycenter.com");

        await _page.WaitForSelectorAsync("#usernamex");
    }

    public async Task CloseAsync()
    {
        if (_browser != null)
        {
            await _browser.CloseAsync();
        }

        _browser = null;
        _page = null;
        _playwright?.Dispose();
        _playwright = null;
    }

    public async Task LoginAsync(string username, string password)
    {
        if (_page == null)
            throw new Exception("Browser has not been launched.");

        await SubmitLoginAsync(username, password);

        try
        {
            await WaitForAccountListAsync();
        }
        catch (TimeoutException)
        {
            // My Energy Center occasionally completes authentication slowly. Reload once
            // and submit the same credentials again before treating it as a real failure.
            await _page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            if (await _page.Locator("#accountList").CountAsync() == 0)
                await SubmitLoginAsync(username, password);

            try
            {
                await WaitForAccountListAsync();
            }
            catch (TimeoutException)
            {
                throw new Exception("Login did not finish after two attempts. My Energy Center was reloaded once, but the account list did not appear.");
            }
        }
    }

    private async Task SubmitLoginAsync(string username, string password)
    {
        if (_page == null)
            throw new Exception("Browser has not been launched.");

        await _page.WaitForSelectorAsync("#usernamex", new PageWaitForSelectorOptions { Timeout = LoginCompletionTimeoutMs });
        await _page.FillAsync("#usernamex", username);
        await _page.WaitForSelectorAsync("#passwordx", new PageWaitForSelectorOptions { Timeout = LoginCompletionTimeoutMs });
        await _page.FillAsync("#passwordx", password);
        await _page.ClickAsync("#btnlogin");
    }

    private async Task WaitForAccountListAsync()
    {
        if (_page == null)
            throw new Exception("Browser has not been launched.");

        await _page.WaitForSelectorAsync("#accountList", new PageWaitForSelectorOptions
        {
            Timeout = LoginCompletionTimeoutMs
        });
    }
    public async Task SelectAccountAsync(string accountText)
    {
        if (_page == null)
            throw new Exception("Browser has not been launched.");

        await _page.WaitForSelectorAsync("#accountList");

        await _page.SelectOptionAsync("#accountList",
            new SelectOptionValue
            {
                Label = accountText
            });

        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    public async Task<IReadOnlyList<string>> GetPropertyAddressesAsync()
    {
        if (_page == null)
            throw new Exception("Browser has not been launched.");

        await _page.WaitForSelectorAsync("#accountList");

        var options = _page.Locator("#accountList option");
        var count = await options.CountAsync();
        var addresses = new List<string>();

        for (var index = 0; index < count; index++)
        {
            var option = options.Nth(index);
            var value = (await option.GetAttributeAsync("value"))?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(value))
                continue;

            var address = (await option.GetAttributeAsync("address"))?.Trim();
            var text = (await option.TextContentAsync())?.Trim();
            var propertyAddress = !string.IsNullOrEmpty(address) ? address : text;

            if (!string.IsNullOrEmpty(propertyAddress))
                addresses.Add(propertyAddress);
        }

        return addresses.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string> SelectPropertyAsync(string propertyText)
    {
        if (_page == null)
            throw new Exception("Browser not launched.");

        propertyText = NormalizePropertySearch(propertyText);

        if (string.IsNullOrEmpty(propertyText))
            throw new ArgumentException("Property text cannot be empty.", nameof(propertyText));

        await _page.WaitForSelectorAsync("#accountList");

        var options = _page.Locator("#accountList option");
        int count = await options.CountAsync();

        var normalizedSearch = propertyText.Trim();
        var matchedOptionValues = new List<string>();

        for (int i = 0; i < count; i++)
        {
            var option = options.Nth(i);
            var text = (await option.TextContentAsync())?.Trim() ?? string.Empty;
            var address = (await option.GetAttributeAsync("address"))?.Trim() ?? string.Empty;
            var dataContent = (await option.GetAttributeAsync("data-content"))?.Trim() ?? string.Empty;
            var ariaLabel = (await option.GetAttributeAsync("aria-label"))?.Trim() ?? string.Empty;
            var value = (await option.GetAttributeAsync("value"))?.Trim() ?? string.Empty;

            var inspectText = string.Join(" | ", new[] { address, dataContent, ariaLabel, text, value }.Where(s => !string.IsNullOrEmpty(s)));
            if (!string.IsNullOrEmpty(inspectText))
                matchedOptionValues.Add(inspectText);

            var matchSources = new[] { address, dataContent, ariaLabel, text, value };
            if (matchSources.Any(source => source.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrEmpty(value))
                    throw new Exception($"Matching option found for '{propertyText}', but it has no value attribute.");

                await _page.SelectOptionAsync("#accountList", value);

                await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                return !string.IsNullOrEmpty(address) ? address :
                    !string.IsNullOrEmpty(text) ? text : normalizedSearch;
            }
        }

        var availableOptions = string.Join(", ", matchedOptionValues.Take(10));
        throw new Exception($"Property '{propertyText}' not found. Available options: {availableOptions}");
    }

    private static string NormalizePropertySearch(string? propertyText)
    {
        var normalized = propertyText?.Trim() ?? string.Empty;

        // Grossmont shorthand, for example: "8485 B" -> "8485 BUCKLAND ST"
        // and "8462 P" -> "8462 PARKWAY DR".
        var grossmontMatch = Regex.Match(normalized, "^(?<number>\\d+)\\s+(?<street>[A-Za-z])$");
        if (grossmontMatch.Success &&
            GrossmontStreetAbbreviations.TryGetValue(grossmontMatch.Groups["street"].Value, out var streetName))
        {
            return $"{grossmontMatch.Groups["number"].Value} {streetName}";
        }

        // Utopia convention: a unit number alone represents an El Cajon address.
        if (Regex.IsMatch(normalized, "^\\d+$"))
            return normalized + ", El Cajon";

        return normalized;
    }

    /// <summary>
    /// Saves the detailed bill for the most recent billing period.
    /// </summary>
    public async Task<string> DownloadLatestBillAsync(string downloadFolder, string selectedAddress)
    {
        if (_page == null)
            throw new Exception("Browser has not been launched.");

        await OpenCurrentBillAsync();
        try
        {
            await WaitForBillContentAsync();
        }
        catch (TimeoutException)
        {
            throw new NoBillAvailableException("No bill details are available for this property.");
        }

        var noBillNotice = _page.GetByText(
            new Regex("billing\\s+information\\s+will\\s+start\\s+displaying\\s+with\\s+your\\s+next\\s+bill", RegexOptions.IgnoreCase));
        if (await noBillNotice.CountAsync() > 0)
            throw new NoBillAvailableException("This property has no bill yet.");

        var detailedBillLink = await FindVisibleTextAsync(
            new Regex("view\\s+your\\s+detailed\\s+bill\\s+pdf|view\\s+your\\s+detailed\\s+pdf", RegexOptions.IgnoreCase),
            TimeSpan.FromSeconds(30));
        if (detailedBillLink == null)
            throw new NoBillAvailableException("No detailed bill PDF is available for this property.");

        var directUrl = await detailedBillLink.GetAttributeAsync("href");
        if (Uri.TryCreate(_page.Url, UriKind.Absolute, out var pageUri) &&
            Uri.TryCreate(directUrl, UriKind.RelativeOrAbsolute, out var billUri))
        {
            var absoluteBillUri = billUri.IsAbsoluteUri ? billUri : new Uri(pageUri, billUri);
            if (absoluteBillUri.Scheme == Uri.UriSchemeHttp || absoluteBillUri.Scheme == Uri.UriSchemeHttps)
                return await SaveBillUrlAsync(_page, absoluteBillUri.ToString(), downloadFolder, selectedAddress);
        }

        var downloadTask = _page.WaitForDownloadAsync(new PageWaitForDownloadOptions { Timeout = 15_000 });
        var popupTask = _page.WaitForPopupAsync(new PageWaitForPopupOptions { Timeout = 15_000 });

        await detailedBillLink.ClickAsync();

        var completed = await Task.WhenAny(downloadTask, popupTask, Task.Delay(15_000));
        if (completed == downloadTask)
        {
            var download = await downloadTask;
            var path = CreateBillPath(downloadFolder, selectedAddress);
            await download.SaveAsAsync(path);
            return path;
        }

        if (completed == popupTask)
        {
            var billPage = await popupTask;
            await billPage.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            return await SaveOpenedBillAsync(billPage, downloadFolder, selectedAddress);
        }

        throw new NoBillAvailableException("My Energy Center did not return a bill PDF for this property.");
    }

    private async Task OpenCurrentBillAsync()
    {
        if (_page == null)
            throw new Exception("Browser has not been launched.");

        var viewBill = await FindVisibleTextAsync(new Regex("^\\s*view\\s+bill\\s*$", RegexOptions.IgnoreCase));
        if (viewBill != null)
        {
            await viewBill.ClickAsync();
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            return;
        }

        var billingMenu = await FindVisibleTextAsync(new Regex("^\\s*billing\\s*$", RegexOptions.IgnoreCase));
        if (billingMenu != null)
            await billingMenu.HoverAsync();

        var yourBill = await FindVisibleTextAsync(new Regex("^\\s*your\\s+bill\\s*$", RegexOptions.IgnoreCase));
        if (yourBill == null)
            throw new Exception("Could not find the View Bill or Your Bill navigation item.");

        await yourBill.ClickAsync();
        await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
    }

    private async Task<ILocator?> FindVisibleTextAsync(Regex pattern, TimeSpan? timeout = null)
    {
        if (_page == null)
            throw new Exception("Browser has not been launched.");

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.Zero);
        do
        {
            var candidates = _page.Locator("a").Filter(new LocatorFilterOptions { HasTextRegex = pattern });
            var count = await candidates.CountAsync();
            for (var index = 0; index < count; index++)
            {
                var candidate = candidates.Nth(index);
                if (await candidate.IsVisibleAsync())
                    return candidate;
            }

            if (timeout is null || DateTime.UtcNow >= deadline)
                break;

            await Task.Delay(500);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    private async Task WaitForBillContentAsync()
    {
        if (_page == null)
            throw new Exception("Browser has not been launched.");

        await _page.WaitForFunctionAsync(
            @"() => {
                const text = document.body?.innerText ?? '';
                return /billing\s+information\s+will\s+start\s+displaying\s+with\s+your\s+next\s+bill|view\s+your\s+detailed\s+(?:bill\s+)?pdf/i.test(text);
            }",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }

    private async Task<string> SaveOpenedBillAsync(IPage billPage, string downloadFolder, string selectedAddress)
    {
        if (_playwright == null)
            throw new Exception("Browser automation is not available.");

        var billUrl = billPage.Url;
        if (!Uri.TryCreate(billUrl, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new Exception("The detailed bill opened without a downloadable PDF address.");
        }

        return await SaveBillUrlAsync(billPage, billUrl, downloadFolder, selectedAddress);
    }

    private async Task<string> SaveBillUrlAsync(IPage sourcePage, string billUrl, string downloadFolder, string selectedAddress)
    {
        if (_playwright == null)
            throw new Exception("Browser automation is not available.");

        // A PDF viewer can request its document in byte ranges. Requesting the opened
        // tab's URL afresh, with the authenticated session cookies, gets the complete PDF.
        var cookies = await sourcePage.Context.CookiesAsync(new[] { billUrl });
        var cookieHeader = string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
        var requestContext = await _playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Cookie"] = cookieHeader
            }
        });

        var path = CreateBillPath(downloadFolder, selectedAddress);
        try
        {
            var response = await requestContext.GetAsync(billUrl);
            if (!response.Ok)
                throw new Exception($"Could not save the detailed bill (HTTP {response.Status}).");

            var pdfBytes = await response.BodyAsync();
            if (pdfBytes.Length == 0)
                throw new Exception("My Energy Center returned an empty bill PDF.");

            await File.WriteAllBytesAsync(path, pdfBytes);
            return path;
        }
        finally
        {
            await requestContext.DisposeAsync();
        }
    }

    private static string CreateBillPath(string downloadFolder, string selectedAddress)
    {
        if (string.IsNullOrWhiteSpace(downloadFolder))
            throw new ArgumentException("Choose a folder to save the bill PDF.", nameof(downloadFolder));

        var directory = Path.GetFullPath(downloadFolder.Trim());

        Directory.CreateDirectory(directory);

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var filename = new string(selectedAddress
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .Trim(' ', '.');

        if (string.IsNullOrEmpty(filename))
            filename = "SDGE-latest-bill";

        filename += ".pdf";

        return Path.Combine(directory, filename);
    }
}
