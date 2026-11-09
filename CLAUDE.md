# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

**Flipkart Price Updater**: a Windows desktop app (C#, .NET 8, Windows Forms) for the Flipkart seller
account **TRUSHAENTERPRISE51** (Trusha Enterprise). The user enters several SKU IDs with new amounts in a grid
and clicks OK. The app drives the user's **already-open, already-logged-in Chrome tab** on
https://seller.flipkart.com/ and updates each SKU on **My Listings**.

The user runs it from **Visual Studio 2022** (open `FlipkartTools.sln`, F5) on Windows. They are not a developer, so
keep instructions step by step, give exact clicks, and avoid jargon.

## Layout

```
FlipkartTools.sln                 Visual Studio solution (CRLF line endings)
FlipkartPriceUpdater/
  FlipkartPriceUpdater.csproj     net8.0-windows, UseWindowsForms, Microsoft.Playwright 1.63.0
  Program.cs                      entry point
  MainForm.cs                     UI: grid (SKU / Price / MRP / Status), "Change price using" selector,
                                  "Use my open Chrome" checkbox, "Connect my Chrome (one-time setup)" button, log box
  SellerHub.cs                    all browser automation (Playwright): attach, navigate, search, update, verify
  PriceParser.cs                  input validation + Excel/CSV paste parsing
  PriceChange.cs                  PriceChange model + PriceMethod enum
  README.md                       end-user instructions (keep in sync with behaviour changes)
```

## How it connects to the user's Chrome

- The user's **normal Chrome profile** is used, not a separate browser. Chrome 136+ refuses `--remote-debugging-port`
  on the default profile, so the app relies on Chrome 144+'s built-in switch: the user turns on
  **"Allow remote debugging for this browser instance"** at `chrome://inspect/#remote-debugging` (one time).
  Chrome then asks the user to **Allow** each connection.
- In that mode Chrome writes `port` + `/devtools/browser/<id>` to
  `%LOCALAPPDATA%\Google\Chrome\User Data\DevToolsActivePort`. Connect to `ws://127.0.0.1:<port><path>` with
  `ConnectOverCDPAsync`. `http://127.0.0.1:<port>/json` returns 404 in this mode, so don't rely on it.
  The `http://127.0.0.1:9222` fallback is only for a Chrome started with `--remote-debugging-port=9222`.
- The app picks the tab whose URL contains `seller.flipkart.com`. On dispose it only disconnects: **never close the
  user's browser or tabs**.
- Unticking "Use my open Chrome" falls back to `StartAsync()`, which launches its own persistent-profile browser
  (`browser_profile/`). This is legacy; the user wants the attach mode.

## Seller Hub UI facts (from the user's screenshots and screen recording)

- **My Listings URL:** `https://seller.flipkart.com/index.html#dashboard/listings-management?listingState=ACTIVE`.
  The left sidebar **Listings** item opens a hover flyout: *My Listings / Add New Listings / Track Approval Requests /
  Selection Insights*.
- **Search:** the top bar ("Search LID", rotating placeholder text) opens a panel with **This Page | All Pages** and an
  input "Search by Title, FSN or SKU ID". Enter runs the search. The resulting URL is
  `...listingState=ACTIVE&listingsSearchQuery=<url-encoded SKU>&partnerContext=ALL`, which the app opens directly
  first and falls back to the UI search.
- **Result row** (div-based, not a `<table>`): Product Details (category, name link, SKU text with dashed underline),
  **Price & Settlement** box `Listing: ₹373 [wallet] ₹269 / Final: ₹373 [wallet] ₹269` with a **›** arrow, Stock,
  Returns, Listing Quality, Additional Info, Actions (its own pencil, which is NOT the price editor).
- **Pencil method:** hovering the Price & Settlement box shows a pencil with an "Edit" tooltip. What it opens has
  **not been seen yet**; that code path is a best guess.
- **Arrow method (the user's main flow, `PriceMethod.BankSettlementArrow`, the default):**
  1. The › arrow opens a **Listing Details** side panel (URL gains `&listingTab=price-details`) on the
     **Settlement & Price** tab. It shows a grey loading skeleton for about 3-4 s.
  2. Shows `Current Pricing  Bank Settlement: ₹269  Listing Price: ₹373`.
  3. **Settlement to Price Calculator:** the **Bank Settlement** input ("Chosen by You"). Typing updates Seller Price,
     Customer Logistics Fees and Listing Price, and the button text becomes **"Apply ₹<amount> as Settlement"**.
  4. Clicking it shows a toast: **"Values updated successfully. Your listing updates may take upto 30-45 mins to
     reflect on website/app."** The row and *Current Pricing* keep the **old** value for 30-45 min, so success is
     judged by the toast, not by re-reading the page.
  5. The panel is closed with the X next to "Listing Details".
- Text on the page: "You can increase the settlement only **2 times a day**."

## Safety rules (must keep)

- **Never click "Apply ₹X as Settlement" (or any final Save) more than once per SKU per run.** Retries
  (`MaxAttempts = 3`) are only for steps *before* the final click; after it, outcomes are OK or CHECK, never FAILED,
  so `UpdateAsync` won't retry them.
- Skip a SKU whose current Bank Settlement already equals the target (saves a daily increase).
- Verify the Apply button shows the same amount that was typed before clicking.
- Report Flipkart's own error text as `CHECK - Flipkart said: ...` instead of retrying.
- Validate input before touching the browser: numeric prices, selling price ≤ MRP, no duplicate SKUs.
- Status prefixes drive the UI colours and retry logic: `OK` (green), `CHECK` (orange, user verifies),
  `FAILED` (red, retried, screenshot saved to `logs/` next to the exe).
- Locate elements by visible text, roles and labels, not by Flipkart's generated class names/ids. The UI changes often.

## Build & test

- The real project builds only on Windows (`dotnet build`, or Visual Studio). On Linux the Ubuntu .NET SDK lacks the
  WindowsDesktop targets, so `dotnet build` of the csproj fails there. To compile-check on Linux, use a scratch
  `net8.0` project that `<Compile Include>`s `FlipkartPriceUpdater/*.cs`, references
  `Microsoft.WindowsDesktop.App.Ref` (PackageDownload + `<Reference>` to its `ref/net8.0/*.dll`),
  adds `<Using Include="System.Drawing" />` and `<Using Include="System.Windows.Forms" />`, and builds with
  `-p:NoWarn=CA1416`.
- Automation is tested against **local mock HTML pages** that imitate Seller Hub (hover flyout, hash routing with
  `listingsSearchQuery`, div rows, Price & Settlement box, Listing Details panel with skeleton, Apply button and
  toast, a 2-increases limit), driven by a console project that compiles `SellerHub.cs`, `PriceParser.cs` and
  `PriceChange.cs`. To exercise the real attach path, start headless Chromium with `--remote-debugging-port=0
  --user-data-dir=<dir>` (it writes `DevToolsActivePort` there) and set `SellerHub.ChromeUserDataDirs`,
  `TabUrlContains` and `MyListingsUrl` to point at the mock. Headless Chromium accepts only one start URL; open
  more tabs with `PUT /json/new?<url>`.
- There is no access to the user's real Seller Hub from the dev environment. Ask the user for screenshots or a
  screen recording of any screen not described above, and don't claim real-site behaviour you haven't seen.

## Conventions

- Keep `FlipkartPriceUpdater/README.md` (user-facing) updated when behaviour changes.
- Keep `bin/`, `obj/`, `browser_profile/` and `logs/` out of git (see `FlipkartPriceUpdater/.gitignore`).
- Don't put model names or identifiers in commits or code.
