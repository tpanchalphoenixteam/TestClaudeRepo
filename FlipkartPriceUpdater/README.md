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
The app works in **the Chrome you already use**, on the Flipkart Seller Hub tab that is already logged in.
It does not open a new browser and you don't log in again.

**One-time setup (Chrome 144 or newer)**
1. Click **Connect my Chrome (one-time setup)** in the app, or open `chrome://inspect/#remote-debugging` in Chrome yourself.
2. Turn ON **"Allow remote debugging for this browser instance"**.

**Every time**
1. Have Seller Hub open in Chrome as usual.
2. Fill the grid: **SKU ID**, **Selling Price**, optional **MRP**. Or copy the columns from Excel and press **Ctrl+V** in the grid.
3. Click **OK - Update Prices**. **Chrome asks to allow the connection: click Allow.**
4. The app finds your Seller Hub tab and, for each SKU, goes **Listings → My Listings**, searches the SKU ID,
   changes the price and clicks Save.
5. Each row's **Status** column turns green (OK), orange (CHECK = please verify in the browser) or red (FAILED).
   A screenshot of every problem is saved in the `logs` folder next to the app
   (`bin\Debug\net8.0-windows\logs` when run from Visual Studio). Chrome and your tabs stay open.

Before anything runs, the app checks: prices are numbers, selling price ≤ MRP, no duplicate SKUs.

How it connects: while the setting is on, Chrome writes its connection address to
`%LOCALAPPDATA%\Google\Chrome\User Data\DevToolsActivePort`, and the app connects there with Microsoft.Playwright.
You can turn the setting off again at the same `chrome://inspect` page.

## Files
| File | Purpose |
|---|---|
| `MainForm.cs` | The window (grid, Excel paste, OK button, status colours, log) |
| `SellerHub.cs` | Browser automation with Microsoft.Playwright (login, find SKU, set price, save) |
| `PriceParser.cs` | Input validation and Excel/CSV paste parsing |

Flipkart changes its Seller Hub screens often, so fields are found by their on-screen labels. If a step keeps failing,
send the screenshot from `logs` so the selectors can be adjusted.
