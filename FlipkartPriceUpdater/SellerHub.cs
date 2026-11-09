using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace FlipkartPriceUpdater;

/// <summary>
/// Drives Flipkart Seller Hub in a visible browser window.
/// Fields are found by their on-screen labels (not fixed ids) because Flipkart changes its UI often.
/// </summary>
public sealed class SellerHub : IAsyncDisposable
{
    const string SellerHubUrl = "https://seller.flipkart.com/";
    const RegexOptions I = RegexOptions.IgnoreCase;

    // "Manage listings" pages; the first one that shows a SKU search box is used.
    static readonly string[] ListingPages =
    {
        "https://seller.flipkart.com/index.html#dashboard/listings-management",
        "https://seller.flipkart.com/index.html#dashboard/my-listings",
    };
    static readonly string[] SaveButtons = { "Save", "Update", "Save Changes", "Update Price", "Submit", "Done" };
    static readonly string[] PriceLabels = { "Your selling price", "Selling Price", "Your Selling Price (INR)" };
    static readonly Regex SuccessRx = new("success|updated|saved|submitted", I);
    const string ControlSelector = "input:not([type=hidden]):not([type=file]), select, textarea, [role=combobox]";

    readonly Func<string, Task> _askUser;
    readonly Action<string> _log;
    IPlaywright? _playwright;
    IBrowserContext? _context;
    IPage? _page;

    /// <param name="askUser">Shows a message and returns when the user has done a manual step.</param>
    /// <param name="log">Progress log.</param>
    public SellerHub(Func<string, Task> askUser, Action<string> log)
    {
        _askUser = askUser;
        _log = log;
    }

    public string ProfileDir { get; init; } = Path.Combine(AppContext.BaseDirectory, "browser_profile");
    public string LogDir { get; init; } = Path.Combine(AppContext.BaseDirectory, "logs");
    public string? ExecutablePath { get; init; }   // for testing with a specific browser
    public bool Headless { get; init; }
    public int DebugPort { get; init; } = 9222;
    public string TabUrlContains { get; init; } = "seller.flipkart.com";

    IPage Page => _page ?? throw new InvalidOperationException("Call StartAsync or AttachToOpenChromeAsync first");
    IBrowser? _attachedBrowser;
    bool Attached => _attachedBrowser is not null;

    /// <summary>Chrome profile folders whose DevToolsActivePort file is checked (your normal Chrome first).</summary>
    public IReadOnlyList<string> ChromeUserDataDirs { get; init; } = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data"),
    };

    // ------------------------------------------------------------------ attach to the user's open Chrome
    /// <summary>
    /// Connects to your already-running Chrome and picks the tab that already has Seller Hub open.
    /// Works with your normal Chrome once "Allow remote debugging" is switched on at
    /// chrome://inspect/#remote-debugging (Chrome 144+; Chrome asks you to Allow each connection),
    /// or with a Chrome started with --remote-debugging-port=9222. Returns false if neither is available.
    /// </summary>
    public async Task<bool> AttachToOpenChromeAsync()
    {
        _playwright ??= await Playwright.CreateAsync();
        foreach (var endpoint in CandidateEndpoints())
        {
            var isWs = endpoint.StartsWith("ws://", StringComparison.Ordinal);
            try
            {
                if (isWs) _log("Connecting to your Chrome - if Chrome asks to allow remote debugging, click Allow.");
                // Long timeout for the WebSocket: Chrome waits for you to click Allow.
                _attachedBrowser = await _playwright.Chromium.ConnectOverCDPAsync(
                    endpoint, new() { Timeout = isWs ? 90_000 : 5_000 });
                break;
            }
            catch (Exception)
            {
                _attachedBrowser = null;   // stale port file / Chrome not listening / not allowed - try the next one
            }
        }
        if (_attachedBrowser is null) return false;

        var tabs = _attachedBrowser.Contexts.SelectMany(c => c.Pages).ToList();
        _page = tabs.FirstOrDefault(p => p.Url.Contains(TabUrlContains, StringComparison.OrdinalIgnoreCase));
        if (_page is null)
        {
            var ctx = _attachedBrowser.Contexts.FirstOrDefault() ?? await _attachedBrowser.NewContextAsync();
            _page = await ctx.NewPageAsync();
            _log("No Flipkart tab was open - opening Seller Hub in a new tab.");
            await _page.GotoAsync(SellerHubUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        }
        else
        {
            _log($"Using your open Flipkart tab: {_page.Url}");
        }
        await _page.BringToFrontAsync();
        _page.SetDefaultTimeout(15_000);
        return true;
    }

    /// <summary>
    /// Chrome writes "port\n/devtools/browser/&lt;id&gt;" to DevToolsActivePort while remote debugging is on.
    /// In the chrome://inspect toggle mode only that WebSocket path works (http://host:port/json returns 404).
    /// </summary>
    IEnumerable<string> CandidateEndpoints()
    {
        var seen = new HashSet<string>();
        foreach (var dir in ChromeUserDataDirs)
        {
            string[] lines;
            try
            {
                using var fs = new FileStream(Path.Combine(dir, "DevToolsActivePort"), FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (lines.Length >= 2 && int.TryParse(lines[0], out var port) && lines[1].StartsWith('/'))
            {
                var ws = $"ws://127.0.0.1:{port}{lines[1]}";
                if (seen.Add(ws)) yield return ws;
            }
        }
        yield return $"http://127.0.0.1:{DebugPort}";
    }

    /// <summary>Opens chrome://inspect/#remote-debugging in your Chrome (one-time setup page).</summary>
    public static void OpenRemoteDebuggingSettings()
    {
        var exe = FindChromeExe() ?? throw new FileNotFoundException("Google Chrome is not installed (chrome.exe not found).");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            Arguments = "chrome://inspect/#remote-debugging",
            UseShellExecute = false,
        });
    }

    static string? FindChromeExe()
    {
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        return roots.Where(r => r.Length > 0)
                    .Select(r => Path.Combine(r, "Google", "Chrome", "Application", "chrome.exe"))
                    .FirstOrDefault(File.Exists);
    }

    // ------------------------------------------------------------------ separate browser (old mode)
    public async Task StartAsync(bool preferInstalledChrome = true)
    {
        _playwright = await Playwright.CreateAsync();
        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = Headless,
            ExecutablePath = ExecutablePath,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
        };

        if (ExecutablePath is null && preferInstalledChrome)
        {
            try
            {
                options.Channel = "chrome";
                _context = await _playwright.Chromium.LaunchPersistentContextAsync(ProfileDir, options);
            }
            catch (PlaywrightException)
            {
                _log("Google Chrome not found - using Playwright's Chromium instead.");
                options.Channel = null;
            }
        }

        if (_context is null)
        {
            try
            {
                _context = await _playwright.Chromium.LaunchPersistentContextAsync(ProfileDir, options);
            }
            catch (PlaywrightException) when (ExecutablePath is null)
            {
                _log("Downloading Chromium browser (one time, ~150 MB)...");
                var code = await Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
                if (code != 0) throw new InvalidOperationException("Could not download Chromium. Install Google Chrome and try again.");
                _context = await _playwright.Chromium.LaunchPersistentContextAsync(ProfileDir, options);
            }
        }

        _page = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync();
        _page.SetDefaultTimeout(15_000);
    }

    public async Task EnsureLoggedInAsync()
    {
        // When attached to the user's Flipkart tab, stay on it instead of reloading.
        if (!Attached || !Page.Url.Contains(TabUrlContains, StringComparison.OrdinalIgnoreCase))
        {
            await Page.GotoAsync(SellerHubUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Page.WaitForTimeoutAsync(3000);
        }
        var loginBox = await FirstVisibleAsync(Page.GetByPlaceholder(new Regex("password|mobile|email|username", I)));
        if (Page.Url.Contains("login", StringComparison.OrdinalIgnoreCase) || loginBox is not null)
        {
            _log("Waiting for you to log in...");
            await _askUser("Please log in to Flipkart Seller Hub in the browser window (OTP / password).\n\n" +
                           "Click OK here when you can see your Seller Hub dashboard.\n" +
                           "(You only need to do this the first time.)");
        }
    }

    public async Task<ILocator> OpenListingsPageAsync()
    {
        // 1) the way you'd do it by hand: Listings -> My Listings
        _log("Opening Listings -> My Listings...");
        if (await ClickTextAsync("Listings"))
        {
            await ClickTextAsync("My Listings", "Manage Listings", "Active Listings", "Listings Management");
            await Page.WaitForTimeoutAsync(2500);
            if (await FindSkuSearchAsync() is { } box) return Remember(box);
        }

        // 2) known direct links
        foreach (var url in ListingPages)
        {
            await Page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Page.WaitForTimeoutAsync(3000);
            if (await FindSkuSearchAsync() is { } box2) return Remember(box2);
        }

        // 3) ask the user
        await _askUser("I couldn't open My Listings automatically.\n\n" +
                       "In the Flipkart tab, click Listings -> My Listings (the page with the SKU search box), then click OK here.");
        return Remember(await FindSkuSearchAsync() ?? throw new InvalidOperationException("SKU search box not found on this page"));
    }

    string? _listingsUrl;

    ILocator Remember(ILocator searchBox)
    {
        _listingsUrl = Page.Url;
        return searchBox;
    }

    // ------------------------------------------------------------------ price update
    /// <summary>Updates one SKU and sets <see cref="PriceChange.Status"/>. Never throws.</summary>
    public async Task UpdateAsync(PriceChange change)
    {
        try
        {
            change.Status = await UpdateCoreAsync(change);
        }
        catch (Exception ex)
        {
            change.Status = $"FAILED - {ex.GetType().Name}: {FirstLine(ex.Message)}";
        }

        if (!change.Succeeded)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                var file = $"{DateTime.Now:yyyyMMdd-HHmmss}_{Regex.Replace(change.Sku, @"\W+", "_")}.png";
                await Page.ScreenshotAsync(new() { Path = Path.Combine(LogDir, file), FullPage = true });
                change.Status += $"  [screenshot: logs\\{file}]";
            }
            catch { /* screenshot is best effort */ }
        }
    }

    async Task<string> UpdateCoreAsync(PriceChange c)
    {
        // Only reuse the search box if we're still on My Listings (the home page has a different "Search LID" box).
        var search = Page.Url == _listingsUrl ? await FindSkuSearchAsync() : null;
        search ??= await OpenListingsPageAsync();
        await search.FillAsync("");
        await search.FillAsync(c.Sku);
        await search.PressAsync("Enter");
        await Page.WaitForTimeoutAsync(2500);

        ILocator? row = null;
        for (var i = 0; i < 6 && row is null; i++)
        {
            row = await FindSkuRowAsync(c.Sku);
            if (row is null) await Page.WaitForTimeoutAsync(1000);
        }
        if (row is null) return "FAILED - SKU not found in listings";

        // A) price editable directly in the listings table
        var priceInput = await FirstVisibleAsync(
            row.GetByLabel(new Regex("selling price|your price|price", I)),
            row.Locator("input[name*=price i], input[id*=price i], input[placeholder*=price i]"));

        if (priceInput is not null)
        {
            await priceInput.FillAsync(c.Price);
            if (c.Mrp is not null &&
                await FirstVisibleAsync(row.Locator("input[name*=mrp i], input[id*=mrp i], input[placeholder*=mrp i]")) is { } mrpInput)
            {
                await mrpInput.FillAsync(c.Mrp);
            }
            await priceInput.PressAsync("Tab");
        }
        else
        {
            // B) open the listing editor from the row
            var edit = await FirstVisibleAsync(
                row.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^\s*edit", I) }),
                row.GetByRole(AriaRole.Link, new() { NameRegex = new Regex(@"^\s*edit", I) }),
                row.GetByText(new Regex(@"^\s*edit\s*(price|listing)?\s*$", I)),
                row.Locator("[title*=edit i], [aria-label*=edit i]"));
            if (edit is null) return "FAILED - no price box or Edit button for this SKU";
            await edit.ClickAsync();
            await Page.WaitForTimeoutAsync(3000);
            await ClickTextAsync("Price, Stock and Shipping Information", "Price, Stock and Shipping", "Price");

            if (c.Mrp is not null && !await SetValueAsync("MRP", c.Mrp)) return "FAILED - MRP field not found";
            var priceSet = false;
            foreach (var label in PriceLabels)
                if (priceSet = await SetValueAsync(label, c.Price)) break;
            if (!priceSet) return "FAILED - selling price field not found";
        }

        await Page.WaitForTimeoutAsync(500);
        ILocator scope = priceInput is not null ? row : Page.Locator("body");
        var saved = false;
        foreach (var name in SaveButtons)
        {
            var btn = await FirstVisibleAsync(scope.GetByRole(AriaRole.Button,
                new() { NameRegex = new Regex($@"^\s*{Regex.Escape(name)}\s*$", I) }));
            if (btn is not null && await btn.IsEnabledAsync())
            {
                await btn.ClickAsync();
                saved = true;
                break;
            }
        }
        if (!saved) return "CHECK - price typed, but no Save/Update button found (save it in the browser)";

        await Page.WaitForTimeoutAsync(2500);
        if (await FirstVisibleAsync(Page.GetByText(SuccessRx)) is not null) return "OK - updated";
        if (await FirstVisibleAsync(Page.Locator("[class*=error i], [role=alert]")) is { } err)
            return $"CHECK - Flipkart said: {FirstLine(await err.InnerTextAsync())}";
        return "OK - saved (no confirmation message seen, please spot-check)";
    }

    // ------------------------------------------------------------------ helpers
    Task<ILocator?> FindSkuSearchAsync() => FirstVisibleAsync(
        Page.GetByPlaceholder(new Regex("sku", I)),
        Page.GetByPlaceholder(new Regex("search", I)),
        Page.Locator("input[type=search]"));

    /// <summary>Find the element showing exactly this SKU, then climb to its table row / card.</summary>
    async Task<ILocator?> FindSkuRowAsync(string sku)
    {
        var rx = new Regex($@"^\s*(SKU( ID)?\s*:?\s*)?{Regex.Escape(sku)}\s*$", I);
        var cell = await FirstVisibleAsync(Page.GetByText(rx));
        if (cell is null) return null;

        var row = cell.Locator("xpath=ancestor::*[self::tr or @role='row'][1]");
        if (await row.CountAsync() > 0) return row.First;

        for (var up = 1; up < 8; up++)   // div-based layouts
        {
            var ancestor = cell.Locator("xpath=" + string.Join("/", Enumerable.Repeat("..", up)));
            if (await ancestor.Locator("button, input:not([type=hidden]), a").CountAsync() > 0) return ancestor;
        }
        return null;
    }

    static async Task<ILocator?> FirstVisibleAsync(params ILocator[] candidates)
    {
        foreach (var loc in candidates)
        {
            int n;
            try { n = await loc.CountAsync(); } catch { continue; }
            for (var i = 0; i < Math.Min(n, 5); i++)
            {
                var item = loc.Nth(i);
                try { if (await item.IsVisibleAsync()) return item; } catch { /* detached */ }
            }
        }
        return null;
    }

    async Task<bool> ClickTextAsync(params string[] texts)
    {
        foreach (var t in texts)
        {
            var rx = new Regex($@"^\s*{Regex.Escape(t)}\s*$", I);
            var loc = await FirstVisibleAsync(
                Page.GetByRole(AriaRole.Button, new() { NameRegex = rx }),
                Page.GetByRole(AriaRole.Link, new() { NameRegex = rx }),
                Page.GetByRole(AriaRole.Tab, new() { NameRegex = rx }),
                Page.GetByText(rx));
            if (loc is null) continue;
            try
            {
                await loc.ClickAsync(new() { Timeout = 4000 });
                await Page.WaitForTimeoutAsync(800);
                return true;
            }
            catch (PlaywrightException) { }
        }
        return false;
    }

    /// <summary>Find the input/select/textarea that belongs to a visible label.</summary>
    async Task<ILocator?> FindFieldAsync(string label)
    {
        var rx = new Regex($@"^\s*{Regex.Escape(label)}\s*\*?\s*(\(.*\))?\s*:?\s*$", I);
        var direct = await FirstVisibleAsync(Page.GetByLabel(rx), Page.GetByPlaceholder(new Regex(Regex.Escape(label), I)));
        if (direct is not null) return direct;

        var labelEl = await FirstVisibleAsync(Page.GetByText(rx));
        if (labelEl is null) return null;

        // Walk up; stop at the first container holding exactly one control.
        for (var up = 1; up < 4; up++)
        {
            var controls = labelEl.Locator("xpath=" + string.Join("/", Enumerable.Repeat("..", up))).Locator(ControlSelector);
            var n = await controls.CountAsync();
            if (n == 1) return controls.First;
            if (n > 1) break;
        }
        var next = labelEl.Locator("xpath=following::*[self::input[not(@type='hidden') and not(@type='file')]" +
                                   " or self::select or self::textarea or @role='combobox'][1]");
        return await next.CountAsync() > 0 ? next : null;
    }

    async Task<bool> SetValueAsync(string label, string value)
    {
        var ctl = await FindFieldAsync(label);
        if (ctl is null) return false;
        try
        {
            await ctl.ScrollIntoViewIfNeededAsync();
            var tag = await ctl.EvaluateAsync<string>("e => e.tagName.toLowerCase()");
            if (tag == "select")
            {
                var options = await ctl.Locator("option").AllInnerTextsAsync();
                var match = options.FirstOrDefault(o => o.Trim().Equals(value, StringComparison.OrdinalIgnoreCase))
                            ?? options.FirstOrDefault(o => o.Contains(value, StringComparison.OrdinalIgnoreCase))
                            ?? value;
                await ctl.SelectOptionAsync(new SelectOptionValue { Label = match });
            }
            else
            {
                await ctl.FillAsync("");
                await ctl.FillAsync(value);
                await ctl.PressAsync("Tab");
            }
            _log($"    {label} = {value}");
            return true;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    static string FirstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length > 120 ? line[..120] : line;
    }

    public async ValueTask DisposeAsync()
    {
        // Attached mode: just disconnect - your Chrome window and Flipkart tab stay open.
        if (_context is not null && !Attached) await _context.CloseAsync();
        _playwright?.Dispose();
    }
}
