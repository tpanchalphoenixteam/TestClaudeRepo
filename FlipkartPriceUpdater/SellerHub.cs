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
    static readonly Regex SuccessRx = new("success|updated|saved|submitted", I);

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

    public const int MaxAttempts = 3;

    /// <summary>Sidebar "Listings" (hover opens a menu) -> "My Listings". Retries, then asks you.</summary>
    public async Task<ILocator> OpenListingsPageAsync()
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            _log(attempt == 1 ? "Opening Listings -> My Listings..." : $"Opening My Listings again (try {attempt}/{MaxAttempts})...");
            try
            {
                if (await OpenMyListingsFromMenuAsync() && await WaitForSkuSearchAsync() is { } box)
                    return Remember(box);
            }
            catch (PlaywrightException ex)
            {
                _log("    " + FirstLine(ex.Message));
            }
            // something went wrong: reload the Seller Hub tab and try again
            try { await Page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded }); } catch (PlaywrightException) { }
            await Page.WaitForTimeoutAsync(2000);
        }

        // known direct links as a last automatic attempt
        foreach (var url in ListingPages)
        {
            try
            {
                await Page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                if (await WaitForSkuSearchAsync() is { } box2) return Remember(box2);
            }
            catch (PlaywrightException) { }
        }

        await _askUser("I couldn't open My Listings automatically.\n\n" +
                       "In the Flipkart tab, hover Listings (left menu) and click My Listings, then click OK here.");
        return Remember(await WaitForSkuSearchAsync() ?? throw new InvalidOperationException("Search box not found on My Listings"));
    }

    async Task<bool> OpenMyListingsFromMenuAsync()
    {
        var myListingsRx = new Regex(@"^\s*My Listings\s*$", I);
        // The sidebar "Listings" opens a flyout menu on hover (My Listings / Add New Listings / ...).
        var sidebar = await FirstVisibleAsync(
            Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex(@"^\s*Listings\s*$", I) }),
            Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^\s*Listings\s*$", I) }),
            Page.GetByText(new Regex(@"^\s*Listings\s*$", I)));
        if (sidebar is not null)
        {
            await sidebar.HoverAsync();
            await Page.WaitForTimeoutAsync(800);
        }

        var item = await FirstVisibleAsync(
            Page.GetByRole(AriaRole.Link, new() { NameRegex = myListingsRx }),
            Page.GetByRole(AriaRole.Menuitem, new() { NameRegex = myListingsRx }),
            Page.GetByText(myListingsRx));
        if (item is null && sidebar is not null)
        {
            await sidebar.ClickAsync();           // some layouts open the menu on click
            await Page.WaitForTimeoutAsync(1000);
            item = await FirstVisibleAsync(Page.GetByRole(AriaRole.Link, new() { NameRegex = myListingsRx }), Page.GetByText(myListingsRx));
        }
        if (item is null) return false;

        await item.ClickAsync();
        try { await Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 10_000 }); } catch (PlaywrightException) { }
        await Page.WaitForTimeoutAsync(1500);
        return true;
    }

    async Task<ILocator?> WaitForSkuSearchAsync()
    {
        for (var i = 0; i < 10; i++)
        {
            if (await FindSkuSearchAsync() is { } box) return box;
            await Page.WaitForTimeoutAsync(1000);
        }
        return null;
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
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                change.Status = await UpdateCoreAsync(change);
            }
            catch (Exception ex)
            {
                change.Status = $"FAILED - {ex.GetType().Name}: {FirstLine(ex.Message)}";
            }
            if (!change.Status.StartsWith("FAILED", StringComparison.Ordinal)) break;   // OK or CHECK: don't redo
            if (attempt < MaxAttempts)
            {
                try { await Page.Keyboard.PressAsync("Escape"); } catch (PlaywrightException) { }
                _log($"    {change.Status} -> retrying ({attempt + 1}/{MaxAttempts})");
                _listingsUrl = null;   // forces a fresh Listings -> My Listings before the next try
                await Page.WaitForTimeoutAsync(2000);
            }
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

    /// <summary>Which control in the "Price &amp; Settlement" box is used to change the price.</summary>
    public PriceMethod Method { get; set; } = PriceMethod.EditPencil;

    static readonly Regex SaveRx = new(@"^\s*(save|update|submit|confirm|done|update price|save changes|save price)\s*$", I);
    static readonly Regex ConfirmRx = new(@"^\s*(yes|confirm|proceed|continue|yes,? update|ok)\s*$", I);

    async Task<string> UpdateCoreAsync(PriceChange c)
    {
        var row = await SearchSkuAsync(c.Sku);
        if (row is null) return "FAILED - SKU not found in My Listings";

        var box = await FindPriceBoxAsync(row);
        if (box is null) return "FAILED - 'Price & Settlement' box not found for this SKU";

        // Mark the inputs that exist now, so we can spot the price box that the click opens.
        await Page.EvaluateAsync("() => document.querySelectorAll('input,textarea').forEach(e => e.setAttribute('data-fpu-old', '1'))");

        var pencil = Method == PriceMethod.EditPencil;
        var error = pencil ? await ClickPencilAsync(box) : await ClickArrowAsync(box);
        if (error is not null) return error;
        await Page.WaitForTimeoutAsync(1200);

        var wanted = pencil ? new Regex(@"listing price|selling price|your price|\bprice\b", I)
                            : new Regex(@"bank settlement|settlement", I);
        var input = await FindNewInputAsync(wanted, requireMatch: false);
        if (input is null)
            return $"FAILED - no price box appeared after clicking {(pencil ? "Edit (pencil)" : "the > arrow")}";

        await input.ClickAsync();
        await input.FillAsync("");
        await input.FillAsync(c.Price);
        if (c.Mrp is not null && await FindNewInputAsync(new Regex(@"\bmrp\b", I), requireMatch: true) is { } mrp)
            await mrp.FillAsync(c.Mrp);
        await Page.WaitForTimeoutAsync(400);

        var clickedSave = await ClickSaveNearAsync(input);
        if (!clickedSave) await input.PressAsync("Enter");   // some inline editors save on Enter
        await Page.WaitForTimeoutAsync(1200);
        await ClickConfirmIfAskedAsync();
        await Page.WaitForTimeoutAsync(1500);

        // Verify: the row's Price & Settlement box now shows the new price.
        if (await RowShowsPriceAsync(c.Sku, c.Price, pencil)) return $"OK - updated, row now shows Rs.{c.Price}";
        if (await FirstVisibleAsync(Page.Locator("[class*=error i], [role=alert]")) is { } err &&
            (await err.InnerTextAsync()).Trim().Length > 0)
            return $"CHECK - Flipkart said: {FirstLine(await err.InnerTextAsync())}";
        if (!clickedSave) return "CHECK - price typed, but no Save/Update button found (save it in the browser)";
        if (await FirstVisibleAsync(Page.GetByText(SuccessRx)) is not null) return "OK - updated (Flipkart showed a success message)";
        return $"CHECK - saved, but the row doesn't show Rs.{c.Price} yet (Flipkart may need a few minutes - please verify)";
    }

    /// <summary>Type the SKU in the top search panel ("This Page") and return the result row.</summary>
    async Task<ILocator?> SearchSkuAsync(string sku)
    {
        var search = Page.Url == _listingsUrl ? await FindSkuSearchAsync() : null;
        search ??= await OpenListingsPageAsync();
        await search.ClickAsync();
        await Page.WaitForTimeoutAsync(700);

        // Clicking opens a panel with "This Page | All Pages" and its own input.
        ILocator? box = null;
        var thisPage = await FirstVisibleAsync(
            Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^\s*This Page\s*$", I) }),
            Page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex(@"^\s*This Page\s*$", I) }),
            Page.GetByText(new Regex(@"^\s*This Page\s*$", I)));
        if (thisPage is not null)
        {
            await thisPage.ClickAsync();
            await Page.WaitForTimeoutAsync(300);
            box = await FirstVisibleAsync(thisPage.Locator("xpath=ancestor::*[.//input][1]").Locator("input:not([type=hidden]):not([type=checkbox])"));
        }
        box ??= await FirstVisibleAsync(Page.Locator("input:focus")) ?? await FindSkuSearchAsync() ?? search;

        await box.ClickAsync();
        await box.FillAsync("");
        await box.FillAsync(sku);
        await Page.WaitForTimeoutAsync(700);
        await box.PressAsync("Enter");
        await Page.WaitForTimeoutAsync(2500);

        for (var i = 0; i < 8; i++)
        {
            if (await FindSkuRowAsync(sku) is { } row) return row;
            if (i == 2)
            {
                // If the search only shows a suggestion ("Search for ..."), click it.
                var suggestion = await FirstVisibleAsync(
                    Page.GetByRole(AriaRole.Option).Filter(new() { HasTextRegex = new Regex(Regex.Escape(sku[..Math.Min(sku.Length, 15)]), I) }),
                    Page.GetByText(new Regex(@"^\s*Search for\b", I)));
                if (suggestion is not null) await suggestion.ClickAsync();
            }
            await Page.WaitForTimeoutAsync(1000);
        }
        return null;
    }

    /// <summary>The "Price &amp; Settlement" box of a row ("Listing: Rs.373 ... Final: Rs.373 ...").</summary>
    static async Task<ILocator?> FindPriceBoxAsync(ILocator row)
    {
        var lines = row.Locator("div, td, section")
            .Filter(new() { HasTextRegex = new Regex(@"Listing\s*:?\s*₹", I) })
            .Filter(new() { HasTextRegex = new Regex(@"Final\s*:?\s*₹", I) });
        var n = await lines.CountAsync();
        if (n == 0) return null;
        // The deepest match holds just the two price lines; its parent also holds the pencil and the > arrow.
        return lines.Nth(n - 1).Locator("xpath=..");
    }

    async Task<string?> ClickPencilAsync(ILocator box)
    {
        await box.HoverAsync();                // the pencil only appears while hovering the box
        await Page.WaitForTimeoutAsync(600);
        var labelled = await FirstVisibleAsync(
            box.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^\s*edit", I) }),
            box.Locator("[title='Edit' i], [aria-label*=edit i], [data-tooltip*=edit i], [class*=pencil i], [class*=edit i]"));
        if (labelled is not null)
        {
            await labelled.ClickAsync();
            return null;
        }

        // Unlabelled icons: hover each one and click the one that shows the "Edit" tooltip.
        var editTip = Page.GetByText(new Regex(@"^\s*Edit\s*$", I));
        var icons = box.Locator("svg, img, button, [role=button]");
        var n = Math.Min(await icons.CountAsync(), 15);
        for (var i = 0; i < n; i++)
        {
            var icon = icons.Nth(i);
            if (!await icon.IsVisibleAsync()) continue;
            var before = await CountVisibleAsync(editTip);
            await icon.HoverAsync();
            await Page.WaitForTimeoutAsync(400);
            if (await CountVisibleAsync(editTip) > before)
            {
                await icon.ClickAsync();
                return null;
            }
        }
        return "FAILED - pencil (Edit) icon not found in the Price & Settlement box";
    }

    async Task<string?> ClickArrowAsync(ILocator box)
    {
        await box.HoverAsync();
        await Page.WaitForTimeoutAsync(400);
        var arrow = await FirstVisibleAsync(box.Locator("[class*=arrow i], [class*=chevron i], [aria-label*=detail i], [aria-label*=more i]"));
        if (arrow is null)
        {
            var svgs = box.Locator("svg");          // the > arrow is the right-most icon in the box
            for (var i = await svgs.CountAsync() - 1; i >= 0 && arrow is null; i--)
                if (await svgs.Nth(i).IsVisibleAsync()) arrow = svgs.Nth(i);
        }
        await (arrow ?? box).ClickAsync();
        return null;
    }

    /// <summary>A visible input that appeared after the click; prefers one whose label matches <paramref name="wanted"/>.</summary>
    async Task<ILocator?> FindNewInputAsync(Regex wanted, bool requireMatch)
    {
        const string labelJs = @"e => {
            let t = [e.getAttribute('aria-label'), e.placeholder, e.name, e.id].filter(Boolean).join(' ');
            if (e.labels) for (const l of e.labels) t += ' ' + l.innerText;
            for (let p = e.parentElement, i = 0; p && i < 4; p = p.parentElement, i++) {
                const txt = (p.innerText || '').trim();
                if (txt) { t += ' ' + txt.slice(0, 120); break; }
            }
            return t; }";
        var inputs = Page.Locator("input:not([data-fpu-old]):not([type=hidden]):not([type=checkbox]):not([type=radio]):not([type=file]), textarea:not([data-fpu-old])");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ILocator? fallback = null;
            var n = Math.Min(await inputs.CountAsync(), 20);
            for (var i = 0; i < n; i++)
            {
                var el = inputs.Nth(i);
                if (!await el.IsVisibleAsync() || !await el.IsEditableAsync()) continue;
                if (wanted.IsMatch(await el.EvaluateAsync<string>(labelJs))) return el;
                fallback ??= el;
            }
            if (fallback is not null && !requireMatch) return fallback;
            await Page.WaitForTimeoutAsync(800);
        }
        return null;
    }

    /// <summary>Clicks the Save/Update button closest to the input (popup, drawer or inline editor).</summary>
    async Task<bool> ClickSaveNearAsync(ILocator input)
    {
        var ancestors = input.Locator("xpath=ancestor::*[.//button or .//*[@role='button']]");
        var n = await ancestors.CountAsync();
        for (var i = n - 1; i >= Math.Max(0, n - 8); i--)      // nearest container first
        {
            var btn = await FirstVisibleAsync(ancestors.Nth(i).GetByRole(AriaRole.Button, new() { NameRegex = SaveRx }));
            if (btn is not null && await btn.IsEnabledAsync())
            {
                await btn.ClickAsync();
                return true;
            }
        }
        return false;
    }

    async Task ClickConfirmIfAskedAsync()
    {
        var dialogs = Page.Locator("[role=dialog], [role=alertdialog], [class*=modal i]");
        var btn = await FirstVisibleAsync(dialogs.GetByRole(AriaRole.Button, new() { NameRegex = ConfirmRx }));
        if (btn is not null)
        {
            await btn.ClickAsync();
            await Page.WaitForTimeoutAsync(1000);
        }
    }

    async Task<bool> RowShowsPriceAsync(string sku, string price, bool listingPrice)
    {
        var p = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var rx = new Regex((listingPrice ? @"Listing:?₹" : "₹") + Regex.Escape(p) + @"(?![\d.])");
        for (var i = 0; i < 4; i++)
        {
            if (await FindSkuRowAsync(sku) is { } row && await FindPriceBoxAsync(row) is { } box)
            {
                var text = Regex.Replace(await box.InnerTextAsync(), @"[\s,]", "");
                if (rx.IsMatch(text)) return true;
            }
            await Page.WaitForTimeoutAsync(1000);
        }
        return false;
    }

    static async Task<int> CountVisibleAsync(ILocator loc)
    {
        var count = 0;
        var n = Math.Min(await loc.CountAsync(), 20);
        for (var i = 0; i < n; i++)
            if (await loc.Nth(i).IsVisibleAsync()) count++;
        return count;
    }

    // ------------------------------------------------------------------ helpers
    /// <summary>The search box on My Listings - Flipkart's top "Search LID" bar (also takes SKU IDs).</summary>
    async Task<ILocator?> FindSkuSearchAsync()
    {
        var found = await FirstVisibleAsync(
            Page.GetByPlaceholder(new Regex("sku", I)),
            Page.GetByPlaceholder(new Regex("search|LID", I)),
            Page.GetByRole(AriaRole.Searchbox),
            Page.Locator("input[type=search]"),
            Page.Locator("[class*=search i] input:not([type=hidden]), input[class*=search i]"));
        if (found is not null) return found;

        // The "Search LID" label may be text drawn over the box rather than a placeholder.
        var label = await FirstVisibleAsync(Page.GetByText(new Regex(@"^\s*Search\s*(LID|SKU|FSN)?\s*$", I)));
        return label is null ? null : await FirstVisibleAsync(
            label.Locator("xpath=ancestor::*[.//input][1]").Locator("input:not([type=hidden]):not([type=checkbox])"));
    }

    /// <summary>Find the element showing exactly this SKU, then climb to its table row / card.</summary>
    async Task<ILocator?> FindSkuRowAsync(string sku)
    {
        var rx = new Regex($@"^\s*(SKU( ID)?\s*:?\s*)?{Regex.Escape(sku)}\s*$", I);
        var cell = await FirstVisibleAsync(Page.GetByText(rx));
        if (cell is null) return null;

        var row = cell.Locator("xpath=ancestor::*[self::tr or @role='row'][1]");
        if (await row.CountAsync() > 0) return row.First;

        // div-based layouts (Seller Hub): climb until the container also holds the "Listing: Rs..." price box
        for (var up = 1; up < 10; up++)
        {
            var ancestor = cell.Locator("xpath=" + string.Join("/", Enumerable.Repeat("..", up)));
            if (await ancestor.GetByText(new Regex(@"Listing\s*:?\s*₹", I)).CountAsync() > 0) return ancestor;
        }
        for (var up = 1; up < 8; up++)   // other layouts: nearest container with a control
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
