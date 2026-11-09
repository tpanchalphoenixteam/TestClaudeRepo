# Flipkart Seller Hub tools (Trusha Enterprise)

Two scripts that drive https://seller.flipkart.com/ in a visible browser window.

| Script | What it does |
|---|---|
| `update_prices.py` | Asks for several **SKU IDs and new prices**, then you click **OK** and it updates each one |
| `flipkart_listing.py` | Creates a new Roti Cover listing from `listing_config.json` (next SKU, price, description, keywords, images) |

## Setup (one time, Windows/Mac/Linux)

```bash
pip install -r requirements.txt
```

```bash
playwright install chromium
```

(Or skip the second command and add `--chrome` when you run a script, to use your installed Google Chrome.)

## Update prices

```bash
python update_prices.py
```

1. A window opens. Type or paste one SKU per line: `SKU, price` (optional third value: MRP).
   Two columns pasted from Excel work too.
   ```
   3X ROTI COVER P2, 279
   3X ROTI COVER P3, 289, 599
   ```
2. Click **OK - Update Prices**.
3. The browser opens Seller Hub. **First time only:** log in there and press Enter in the terminal.
   The login is saved in `browser_profile/`, so later runs go straight to work.
4. For each SKU it searches Manage Listings, sets the selling price and clicks Save/Update.
   A results window shows **OK / CHECK / FAILED** per SKU. Failed SKUs get a screenshot in `logs/`.

Checks before anything runs: prices must be numbers, the selling price can't be above the MRP, and no SKU can be entered twice.

## Create a new listing

Put up to 5 photos in `images/` (`1.jpg` = main image), then:

```bash
python flipkart_listing.py
```

It confirms the next SKU (`3X ROTI COVER P4`, ...), fills the form, and prints what it filled and what you still need to fill by hand.
It only submits if you type exactly `send to QC`. Use `--dry-run` to never submit.

## Note
Flipkart changes the Seller Hub screens often. The scripts find fields by their on-screen labels instead of hard-coded ids,
and when a step can't be automated they pause and ask you to do it by hand. If a step keeps failing, send the screenshot from `logs/`.
