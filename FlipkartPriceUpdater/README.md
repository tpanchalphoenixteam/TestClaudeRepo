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
1. Fill the grid: **SKU ID**, **Selling Price**, optional **MRP**. Or copy the columns from Excel and press **Ctrl+V** in the grid.
2. Click **OK - Update Prices**.
3. A browser opens on Seller Hub (your installed Google Chrome, or Chromium is downloaded automatically the first time).
   **First run only:** log in there, then click OK on the "Action needed" message. The login is saved in `browser_profile`.
4. Each row's **Status** column turns green (OK), orange (CHECK = please verify in the browser) or red (FAILED).
   A screenshot of every problem is saved in the `logs` folder next to the app.

Before anything runs, the app checks: prices are numbers, selling price ≤ MRP, no duplicate SKUs.

## Files
| File | Purpose |
|---|---|
| `MainForm.cs` | The window (grid, Excel paste, OK button, status colours, log) |
| `SellerHub.cs` | Browser automation with Microsoft.Playwright (login, find SKU, set price, save) |
| `PriceParser.cs` | Input validation and Excel/CSV paste parsing |

Flipkart changes its Seller Hub screens often, so fields are found by their on-screen labels. If a step keeps failing,
send the screenshot from `logs` so the selectors can be adjusted.
