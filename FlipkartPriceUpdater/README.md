# Flipkart Price Updater (C# / .NET 8, Windows)

A Windows app: enter several **SKU IDs + new selling prices**, click **OK**, and it updates each SKU's price on
https://seller.flipkart.com/ in a browser window.

## One-time setup
1. Install the **.NET 8 SDK**: https://dotnet.microsoft.com/download/dotnet/8.0 (pick "SDK", Windows x64).
2. Download this repository (green **Code** button → **Download ZIP**) and unzip it.

## Run
Open a Command Prompt in the `FlipkartPriceUpdater` folder (type `cmd` in the folder's address bar), then:

```bash
dotnet run
```

Or open `FlipkartPriceUpdater.csproj` in **Visual Studio 2022** and press **F5**.

To make a standalone `.exe` you can double-click (no SDK needed on that PC):

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Then run `publish\FlipkartPriceUpdater.exe`.

## Using it
The app works in **your open Chrome window** that is already logged in to Seller Hub.

**First time only**
1. Click **Start Chrome for Flipkart** in the app (or double-click `Start-Chrome-For-Flipkart.bat`).
   A Chrome window opens on Seller Hub. **Log in there.** The login is remembered for next time.
2. Keep that Chrome window open. Use it for Flipkart as you normally would.

> Why a separate Chrome window? Since version 136 (2025), Chrome doesn't let any program control your normal
> everyday profile. This dedicated Flipkart window is the only kind of Chrome window the app is allowed to connect to.

**Every time**
1. Fill the grid: **SKU ID**, **Selling Price**, optional **MRP**. Or copy the columns from Excel and press **Ctrl+V** in the grid.
2. Leave **Use my open Chrome (Flipkart tab)** ticked and click **OK - Update Prices**.
3. The app finds your Seller Hub tab, then for each SKU goes **Listings → My Listings**, searches the SKU ID,
   changes the price and clicks Save. If the Flipkart Chrome window isn't open, it starts it for you.
4. Each row's **Status** column turns green (OK), orange (CHECK = please verify in the browser) or red (FAILED).
   A screenshot of every problem is saved in the `logs` folder next to the app
   (`bin\Debug\net8.0-windows\logs` when run from Visual Studio). Your Chrome window stays open afterwards.

Before anything runs, the app checks: prices are numbers, selling price ≤ MRP, no duplicate SKUs.

Untick **Use my open Chrome** to use the old mode instead, where the app opens its own browser.

## Files
| File | Purpose |
|---|---|
| `MainForm.cs` | The window (grid, Excel paste, OK button, status colours, log) |
| `SellerHub.cs` | Browser automation with Microsoft.Playwright (login, find SKU, set price, save) |
| `Start-Chrome-For-Flipkart.bat` | Opens the Flipkart Chrome window the app connects to (same as the button) |
| `PriceParser.cs` | Input validation and Excel/CSV paste parsing |

Flipkart changes its Seller Hub screens often, so fields are found by their on-screen labels. If a step keeps failing,
send the screenshot from `logs` so the selectors can be adjusted.
