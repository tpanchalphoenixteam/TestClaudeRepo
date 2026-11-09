@echo off
rem Opens a Chrome window for Flipkart that the Price Updater can connect to.
rem Log in to Seller Hub in this window once; the login is remembered. Keep it open while using the app.
set "PROFILE=%LOCALAPPDATA%\FlipkartPriceUpdater\ChromeProfile"
set "CHROME=%ProgramFiles%\Google\Chrome\Application\chrome.exe"
if not exist "%CHROME%" set "CHROME=%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"
if not exist "%CHROME%" set "CHROME=%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"
if not exist "%CHROME%" (echo Google Chrome not found & pause & exit /b 1)
start "" "%CHROME%" --remote-debugging-port=9222 --user-data-dir="%PROFILE%" --no-first-run https://seller.flipkart.com/
