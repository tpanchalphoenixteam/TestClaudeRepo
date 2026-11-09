"""
Flipkart Seller Hub - bulk price updater.

Run it, enter as many SKU IDs and new selling prices as you like, click OK,
and it updates each SKU's selling price in Seller Hub.

    python update_prices.py              # window to enter SKUs + prices
    python update_prices.py --chrome     # use installed Google Chrome
    python update_prices.py --console    # type SKUs in the terminal instead of a window

Input format: one SKU per line, SKU then price, separated by a comma or a TAB
(so you can paste two columns straight from Excel). MRP is optional, as a third value:

    3X ROTI COVER P2, 279
    3X ROTI COVER P3, 289, 599

Login: the browser uses its own saved profile (./browser_profile, shared with
flipkart_listing.py). The first time, log in to Seller Hub in the window that
opens; after that you stay logged in and it goes straight to work.
"""

from __future__ import annotations

import argparse
import re
import sys
import time
from dataclasses import dataclass
from pathlib import Path

from playwright.sync_api import Locator, Page, sync_playwright

from flipkart_listing import PROFILE_DIR, click_text, first_visible, set_value

HERE = Path(__file__).resolve().parent
LOG_DIR = HERE / "logs"
SELLER_HUB = "https://seller.flipkart.com/"
# Seller Hub "Manage listings" pages. The first that loads a SKU search box is used.
LISTING_PAGES = [
    "https://seller.flipkart.com/index.html#dashboard/listings-management",
    "https://seller.flipkart.com/index.html#dashboard/my-listings",
]

SAVE_BUTTONS = ("Save", "Update", "Save Changes", "Update Price", "Submit", "Done")
SUCCESS_RX = re.compile(r"success|updated|saved|submitted", re.I)


@dataclass
class Change:
    sku: str
    price: str
    mrp: str | None = None
    status: str = "pending"


# ----------------------------------------------------------------- input
def parse_lines(text: str) -> tuple[list[Change], list[str]]:
    """Parse 'SKU, price[, mrp]' lines. SKUs may contain spaces, so we split on , or TAB."""
    changes, errors = [], []
    for n, raw in enumerate(text.splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        parts = [p.strip() for p in re.split(r"[,\t]", line) if p.strip()]
        if len(parts) < 2:
            errors.append(f"Line {n}: need 'SKU, price' -> {raw!r}")
            continue
        sku, price, *rest = parts
        mrp = rest[0] if rest else None
        nums = [price] + ([mrp] if mrp else [])
        if not all(re.fullmatch(r"\d+(\.\d{1,2})?", x) for x in nums):
            errors.append(f"Line {n}: price/MRP must be a number -> {raw!r}")
            continue
        if mrp and float(price) > float(mrp):
            errors.append(f"Line {n}: selling price {price} is higher than MRP {mrp}")
            continue
        if any(c.sku.lower() == sku.lower() for c in changes):
            errors.append(f"Line {n}: SKU {sku!r} entered twice")
            continue
        changes.append(Change(sku, price, mrp))
    return changes, errors


def ask_gui() -> list[Change] | None:
    import tkinter as tk
    from tkinter import messagebox, scrolledtext

    result: dict = {}
    root = tk.Tk()
    root.title("Flipkart - Update SKU Prices")
    root.geometry("560x460")
    tk.Label(root, justify="left", font=("Segoe UI", 10),
             text="One SKU per line:  SKU, Selling Price   (optional: , MRP)\n"
                  "You can paste two columns from Excel.\n"
                  "Example:   3X ROTI COVER P2, 279").pack(anchor="w", padx=12, pady=(12, 4))
    box = scrolledtext.ScrolledText(root, font=("Consolas", 11), height=16)
    box.pack(fill="both", expand=True, padx=12)
    box.focus_set()

    def on_ok():
        changes, errors = parse_lines(box.get("1.0", "end"))
        if errors:
            messagebox.showerror("Please fix", "\n".join(errors), parent=root)
            return
        if not changes:
            messagebox.showwarning("Nothing to do", "Enter at least one 'SKU, price' line.", parent=root)
            return
        result["changes"] = changes
        root.destroy()

    bar = tk.Frame(root)
    bar.pack(fill="x", padx=12, pady=10)
    tk.Button(bar, text="Cancel", width=12, command=root.destroy).pack(side="right")
    tk.Button(bar, text="OK - Update Prices", width=18, command=on_ok,
              bg="#2874f0", fg="white").pack(side="right", padx=8)
    root.mainloop()
    return result.get("changes")


def ask_console() -> list[Change] | None:
    print("Enter one 'SKU, price' per line (optional ', MRP'). Empty line to finish:")
    lines = []
    while (line := input("  ").strip()):
        lines.append(line)
    changes, errors = parse_lines("\n".join(lines))
    for e in errors:
        print("  skipped:", e)
    return changes or None


def show_results(changes: list[Change], use_gui: bool) -> None:
    text = "\n".join(f"{c.sku}  ->  {c.price}{'  (MRP ' + c.mrp + ')' if c.mrp else ''}   :  {c.status}"
                     for c in changes)
    print("\n" + "=" * 70 + "\n RESULTS\n" + "=" * 70 + "\n" + text + "\n" + "=" * 70)
    if use_gui:
        try:
            import tkinter as tk
            from tkinter import messagebox
            r = tk.Tk(); r.withdraw()
            messagebox.showinfo("Flipkart price update - results", text)
            r.destroy()
        except Exception:
            pass


# ----------------------------------------------------------------- browser
def find_sku_search(page: Page) -> Locator | None:
    return first_visible([
        page.get_by_placeholder(re.compile(r"sku", re.I)),
        page.get_by_placeholder(re.compile(r"search", re.I)),
        page.locator("input[type=search]"),
    ])


def open_listings_page(page: Page) -> Locator:
    for url in LISTING_PAGES:
        page.goto(url, wait_until="domcontentloaded")
        page.wait_for_timeout(3000)
        if (box := find_sku_search(page)):
            return box
    # fall back to the menu
    click_text(page, "Listings")
    click_text(page, "Manage Listings", "Active Listings", "My Listings", "Listings Management")
    page.wait_for_timeout(2500)
    if (box := find_sku_search(page)):
        return box
    input("\n>>> Couldn't find the listings page automatically. Open Listings -> Manage/Active "
          "Listings in the browser (the page with the SKU search box) and press Enter... ")
    box = find_sku_search(page)
    if not box:
        raise RuntimeError("SKU search box not found on this page")
    return box


def ensure_logged_in(page: Page) -> None:
    page.goto(SELLER_HUB, wait_until="domcontentloaded")
    page.wait_for_timeout(3000)
    if "login" in page.url.lower() or first_visible([page.get_by_placeholder(re.compile("password|mobile|email", re.I))]):
        input("\n>>> Please log in to Seller Hub in the browser window, then press Enter... ")


def sku_row(page: Page, sku: str) -> Locator | None:
    """Find the element showing exactly this SKU, then climb to its table row / card."""
    rx = re.compile(rf"^\s*(SKU( ID)?\s*:?\s*)?{re.escape(sku)}\s*$", re.I)
    cell = first_visible([page.get_by_text(rx)])
    if not cell:
        return None
    row = cell.locator("xpath=ancestor::*[self::tr or @role='row'][1]")
    if row.count():
        return row.first
    # div-based layouts: nearest ancestor that also holds a button or input
    for up in range(1, 8):
        anc = cell.locator("xpath=" + "/".join([".."] * up))
        if anc.locator("button, input:not([type=hidden]), a").count():
            return anc
    return None


def update_one(page: Page, search: Locator, c: Change) -> str:
    search.fill("")
    search.fill(c.sku)
    search.press("Enter")
    page.wait_for_timeout(2500)

    row = None
    for _ in range(6):
        if (row := sku_row(page, c.sku)):
            break
        page.wait_for_timeout(1000)
    if not row:
        return "FAILED - SKU not found in listings"

    # A) price editable directly in the listings table
    price_input = first_visible([
        row.get_by_label(re.compile(r"selling price|your price|price", re.I)),
        row.locator("input[name*=price i], input[id*=price i], input[placeholder*=price i]"),
    ])
    # B) otherwise open the listing editor from the row
    if not price_input:
        btn = first_visible([
            row.get_by_role("button", name=re.compile(r"^\s*edit", re.I)),
            row.get_by_role("link", name=re.compile(r"^\s*edit", re.I)),
            row.get_by_text(re.compile(r"^\s*edit\s*(price|listing)?\s*$", re.I)),
            row.locator("[title*=edit i], [aria-label*=edit i]"),
        ])
        if btn:
            btn.click()
            page.wait_for_timeout(3000)
            click_text(page, "Price, Stock and Shipping Information", "Price, Stock and Shipping", "Price")

    if price_input:
        price_input.fill(c.price)
        if c.mrp:
            mrp_in = first_visible([row.locator("input[name*=mrp i], input[id*=mrp i], input[placeholder*=mrp i]")])
            if mrp_in:
                mrp_in.fill(c.mrp)
        price_input.press("Tab")
    else:
        if c.mrp and not set_value(page, "MRP", c.mrp):
            return "FAILED - MRP field not found"
        if not any(set_value(page, lbl, c.price) for lbl in
                   ("Your selling price", "Selling Price", "Your Selling Price (INR)")):
            return "FAILED - selling price field not found"

    page.wait_for_timeout(500)
    scope = row if price_input else page
    saved = False
    for name in SAVE_BUTTONS:
        btn = first_visible([scope.get_by_role("button", name=re.compile(rf"^\s*{re.escape(name)}\s*$", re.I))])
        if btn and btn.is_enabled():
            btn.click()
            saved = True
            break
    if not saved:
        return "CHECK - price typed, but no Save/Update button found (save it in the browser)"

    page.wait_for_timeout(2500)
    if first_visible([page.get_by_text(SUCCESS_RX)]):
        return "OK - updated"
    err = first_visible([page.locator("[class*=error i], [role=alert]")])
    if err:
        return f"CHECK - Flipkart said: {err.inner_text()[:120]!r}"
    return "OK - saved (no confirmation message seen, please spot-check)"


def run_updates(changes: list[Change], use_chrome: bool) -> None:
    LOG_DIR.mkdir(exist_ok=True)
    with sync_playwright() as pw:
        ctx = pw.chromium.launch_persistent_context(
            str(PROFILE_DIR), headless=False, channel="chrome" if use_chrome else None,
            viewport={"width": 1366, "height": 900},
        )
        page = ctx.pages[0] if ctx.pages else ctx.new_page()
        page.set_default_timeout(15_000)
        ensure_logged_in(page)
        search = open_listings_page(page)

        for i, c in enumerate(changes, 1):
            print(f"[{i}/{len(changes)}] {c.sku} -> {c.price} ...", flush=True)
            try:
                c.status = update_one(page, search, c)
            except Exception as e:
                c.status = f"FAILED - {type(e).__name__}: {str(e).splitlines()[0][:100]}"
            if not c.status.startswith("OK"):
                shot = LOG_DIR / f"{time.strftime('%Y%m%d-%H%M%S')}_{re.sub(r'\W+', '_', c.sku)}.png"
                page.screenshot(path=str(shot), full_page=True)
                c.status += f"  [screenshot: logs/{shot.name}]"
            print("    ", c.status, flush=True)
            # go back to the listings page for the next SKU
            if i < len(changes):
                search = find_sku_search(page) or open_listings_page(page)

        input("\n>>> Done. Check the browser if needed, then press Enter to close it... ")
        ctx.close()


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--chrome", action="store_true", help="use installed Google Chrome")
    ap.add_argument("--console", action="store_true", help="enter SKUs in the terminal, not a window")
    args = ap.parse_args()

    use_gui = not args.console
    if use_gui:
        try:
            changes = ask_gui()
        except Exception as e:  # no tkinter / no display
            print(f"(window not available: {e}) - using the terminal instead")
            use_gui, changes = False, ask_console()
    else:
        changes = ask_console()
    if not changes:
        sys.exit("Nothing to update.")

    run_updates(changes, args.chrome)
    show_results(changes, use_gui)


if __name__ == "__main__":
    try:
        main()
    except (KeyboardInterrupt, EOFError):
        print("\nStopped.")
