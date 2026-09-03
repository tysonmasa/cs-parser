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

        var alreadyOnBillDashboard = _page.Url.Contains(
            "/portal/Billdashboard",
            StringComparison.OrdinalIgnoreCase);

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
                if (alreadyOnBillDashboard)
                    await WaitForAccountSwitchAsync(_page);

                if (!alreadyOnBillDashboard)
                {
                    await _page.Locator("a.cancel-btn[onclick*='Billdashboard']").WaitForAsync(
                        new LocatorWaitForOptions
                        {
                            State = WaitForSelectorState.Visible,
                            Timeout = LoginCompletionTimeoutMs
                        });
                }

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
        await WaitForPageLoadingAsync(_page);

        var detailedBillLink = _page.Locator(
            "a[aria-label=\"Click to View Your Detailed Bill PDF\"][ng-click=\"OperationSteps('DownloadPDF')\"]");
        if (!await WaitForBillLinkAsync(detailedBillLink))
            throw new NoBillAvailableException("No bill is available for this property.");

        detailedBillLink = detailedBillLink.First;

        var pdfResponseTask = WaitForPdfResponseAsync(_page.Context);
        var downloadTask = _page.WaitForDownloadAsync(new PageWaitForDownloadOptions { Timeout = 15_000 });
        var popupTask = _page.WaitForPopupAsync(new PageWaitForPopupOptions { Timeout = 15_000 });

        await detailedBillLink.ClickAsync();

        var completed = await Task.WhenAny(pdfResponseTask, downloadTask, popupTask, Task.Delay(60_000));
        if (completed == pdfResponseTask)
        {
            var response = await pdfResponseTask;
            return await SavePdfResponseAsync(response, downloadFolder, selectedAddress);
        }

        if (completed == downloadTask)
        {
            var download = await downloadTask;
            var path = CreateBillPath(downloadFolder, selectedAddress);
            await download.SaveAsAsync(path);

            if (!await IsPdfFileAsync(path))
            {
                File.Delete(path);
                return await SaveBillUrlAsync(_page, download.Url, downloadFolder, selectedAddress);
            }

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

    private static async Task<bool> WaitForBillLinkAsync(ILocator detailedBillLink)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (await detailedBillLink.IsVisibleAsync())
                return true;

            await Task.Delay(500);
        }

        return false;
    }

    private static async Task WaitForPageLoadingAsync(IPage page)
    {
        var preloader = page.Locator("#preloader");
        if (await preloader.CountAsync() == 0)
            return;

        await preloader.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = LoginCompletionTimeoutMs
        });
    }

    private static async Task WaitForAccountSwitchAsync(IPage page)
    {
        var preloader = page.Locator("#preloader");
        try
        {
            await preloader.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5_000
            });
        }
        catch (TimeoutException)
        {
            await Task.Delay(1_000);
            return;
        }

        await preloader.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = LoginCompletionTimeoutMs
        });
    }

    private static bool IsPdfResponse(IResponse response)
    {
        var contentType = response.Headers.TryGetValue("content-type", out var header)
            ? header
            : string.Empty;
        var contentDisposition = response.Headers.TryGetValue("content-disposition", out var disposition)
            ? disposition
            : string.Empty;

        return contentType.Contains("application/pdf", StringComparison.OrdinalIgnoreCase) ||
            (contentType.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase) &&
                contentDisposition.Contains("attachment", StringComparison.OrdinalIgnoreCase)) ||
            response.Url.Contains(".pdf", StringComparison.OrdinalIgnoreCase) ||
            response.Url.Contains("download", StringComparison.OrdinalIgnoreCase) ||
            response.Url.Contains("bill", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IResponse> WaitForPdfResponseAsync(IBrowserContext context)
    {
        var responseTask = new TaskCompletionSource<IResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async void OnResponse(object? sender, IResponse response)
        {
            if (!IsPdfResponse(response))
                return;

            try
            {
                var body = await response.BodyAsync();
                if (IsPdf(body))
                    responseTask.TrySetResult(response);
            }
            catch
            {
                // The response may disappear while the browser is changing pages.
            }
        }

        context.Response += OnResponse;
        try
        {
            return await responseTask.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            context.Response -= OnResponse;
        }
    }

    private static async Task<string> SavePdfResponseAsync(
        IResponse response,
        string downloadFolder,
        string selectedAddress)
    {
        var pdfBytes = await response.BodyAsync();
        if (!IsPdf(pdfBytes))
            throw new Exception("The bill response was not a valid PDF.");

        var path = CreateBillPath(downloadFolder, selectedAddress);
        await File.WriteAllBytesAsync(path, pdfBytes);
        return path;
    }

    private async Task OpenCurrentBillAsync()
    {
        if (_page == null)
            throw new Exception("Browser has not been launched.");

        var exactViewBill = _page.Locator("a.cancel-btn[onclick*='Billdashboard']");
        if (await exactViewBill.CountAsync() > 0 && await exactViewBill.First.IsVisibleAsync())
        {
            var billPageNavigation = _page.WaitForURLAsync(
                "**/portal/Billdashboard**",
                new PageWaitForURLOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = LoginCompletionTimeoutMs
                });

            await exactViewBill.First.ClickAsync();
            try
            {
                await billPageNavigation;
            }
            catch (TimeoutException)
            {
                throw new Exception($"View Bill did not navigate to /portal/Billdashboard. Current page: {_page.Url}");
            }

            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            return;
        }
        await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
    }

    // private async Task<ILocator?> FindVisibleTextAsync(Regex pattern, TimeSpan? timeout = null)
    // {
    //     if (_page == null)
    //         throw new Exception("Browser has not been launched.");

    //     var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.Zero);
    //     do
    //     {
    //         var candidates = _page.Locator("a").Filter(new LocatorFilterOptions { HasTextRegex = pattern });
    //         var count = await candidates.CountAsync();
    //         for (var index = 0; index < count; index++)
    //         {
    //             var candidate = candidates.Nth(index);
    //             if (await candidate.IsVisibleAsync())
    //                 return candidate;
    //         }

    //         if (timeout is null || DateTime.UtcNow >= deadline)
    //             break;

    //         await Task.Delay(500);
    //     }
    //     while (DateTime.UtcNow < deadline);

    //     return null;
    // }

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

        Exception? lastError = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var billUrl = billPage.Url;
            if (Uri.TryCreate(billUrl, UriKind.Absolute, out var parsedUrl) &&
                (parsedUrl.Scheme == Uri.UriSchemeHttp || parsedUrl.Scheme == Uri.UriSchemeHttps))
            {
                try
                {
                    return await SaveBillUrlAsync(billPage, billUrl, downloadFolder, selectedAddress);
                }
                catch (Exception exception)
                {
                    lastError = exception;
                }
            }

            await Task.Delay(1_000);
        }

        throw new Exception(
            "The detailed bill opened, but the PDF was not ready after 60 seconds.",
            lastError);
    }

    private async Task<string> SaveBillUrlAsync(IPage sourcePage, string billUrl, string downloadFolder, string selectedAddress)
    {
        if (_playwright == null)
            throw new Exception("Browser automation is not available.");

        // Reuse the browser's authenticated cookies when requesting the complete PDF.
        var cookies = await sourcePage.Context.CookiesAsync(new[] { billUrl });
        var cookieHeader = string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
        var requestContext = await _playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Accept"] = "application/pdf,application/octet-stream;q=0.9,*/*;q=0.8",
                ["Cookie"] = cookieHeader,
                ["Referer"] = sourcePage.Url
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

            if (!IsPdf(pdfBytes))
                throw new Exception("My Energy Center returned a non-PDF response for the detailed bill.");

            await File.WriteAllBytesAsync(path, pdfBytes);
            return path;
        }
        finally
        {
            await requestContext.DisposeAsync();
        }
    }

    private static async Task<bool> IsPdfFileAsync(string path)
    {
        var bytes = new byte[5];
        await using var stream = File.OpenRead(path);
        var bytesRead = await stream.ReadAsync(bytes);
        return bytesRead == bytes.Length && IsPdf(bytes);
    }

    private static bool IsPdf(byte[] bytes)
    {
        var searchLength = Math.Min(bytes.Length - 4, 1024);
        for (var index = 0; index < searchLength; index++)
        {
            if (bytes[index] == '%' && bytes[index + 1] == 'P' && bytes[index + 2] == 'D' &&
                bytes[index + 3] == 'F' && bytes[index + 4] == '-')
                return true;
        }

        return false;
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
