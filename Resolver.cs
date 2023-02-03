using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.OffScreen;

namespace CefCaptchaResolver
{
    public class Resolver
    {
        private ChromiumWebBrowser _browser;
        private static string _localPath;
        private static int _screenshotCounter = 0;
        private static string _screenshotDirName;
        private static bool _dbg = false;

        public async Task Init(/*CefSettings cefSettings = null,*/ string screenshotsFolder = null)
        {
            var settings = /*cefSettings ?? */new CefSettings
            {
                LogSeverity = LogSeverity.Error
            };
            Cef.Initialize(settings);
            _browser = new ChromiumWebBrowser();
            _browser.LoadError += _onLoadError;
            _browser.AddressChanged += _onAddressChanged;

            _log("browser init");
            while (!_browser.IsBrowserInitialized)
            {
                await Task.Delay(500);
            }

            if (_dbg)
            {
                _initScreenshotDir(screenshotsFolder);
                _browser.ShowDevTools();
            }
        }

        public async Task ProvokeCaptcha()
        {
            _log("provoking captcha");
            await provokeCaptchaPage().ConfigureAwait(false);
            _log("catch captcha");
            await _makeScreenshot("initial");
        }

        private void _initScreenshotDir(string screenshotsFolder)
        {
            _localPath = string.IsNullOrEmpty(screenshotsFolder) ? Path.GetDirectoryName(Assembly.GetExecutingAssembly().CodeBase).Substring("file:/".Length) : screenshotsFolder;
            _screenshotCounter = 0;
            _screenshotDirName = Path.Combine(_localPath, DateTime.Now.ToString("ddMMyyyyHHmmss"));
            if (_dbg && !Directory.Exists(_screenshotDirName))
            {
                Directory.CreateDirectory(_screenshotDirName);
            }
        }

        //private void _packScreenshotDir()
        //{
        //    if (_dbg && Directory.Exists(_screenshotDirName))
        //    {
        //        string archiveName = Path.GetFileNameWithoutExtension(_screenshotDirName) + ".zip";
        //        ZipFile.CreateFromDirectory(_screenshotDirName, archiveName);
        //        _log("screenshots packed!");
        //    }
        //}

        public async Task<string> RetrieveYandexTranslateApiKey()
        {
            await _browser.LoadUrlAsync("https://translate.yandex.ru/").ConfigureAwait(false);
            
            bool captchaRedirect = _browser.Address.Contains("captcha");

            if(captchaRedirect) return "";

            string script = @"
                var match = document.body.innerHTML.match(/sid: '((?:\w|\.)+)',/im);
                if(match){
                    match[1];
                }else{
                    'no sid';
                }
            ";
            JavascriptResponse res = await _browser.EvaluateScriptAsync(script);

            return res?.Result as string;
        }

        public async Task<MemoryStream> RetrieveCaptchaImage()
        {
            IBrowserHost host = _browser.GetBrowserHost();

            while (true)
            {
                bool isErrorPage = await _isChromeErrorPage();
                if (isErrorPage)
                {
                    _browser.Back();
                    await _waitPageLoading();
                    continue;
                }

                Coordinates coordinates = new Coordinates(0, 0);
                bool isFirstStep = await _isElementExist(".CheckboxCaptcha-Checkbox");
                if (isFirstStep)
                {
                    _log("captcha step 1");
                    coordinates = await _getCoordinates(".CheckboxCaptcha-Checkbox");

                    await _mouseMove(host, new Coordinates(0, 0), coordinates);
                    await _click(host, coordinates);
                    await _makeScreenshot("checkboxClick");
                }

                await _waitElement(".AdvancedCaptcha-Image");

                _log("captcha step 2");
                await _makeScreenshot("imageLoaded");
                JavascriptResponse jsRes = await _browser.EvaluateScriptAsync("document.querySelector('.AdvancedCaptcha-Image').src");

                string url = jsRes.Result.ToString();
                MemoryStream stream;
                try
                {
                    stream = _downloadUrl(url);
                }
                catch (Exception ex)
                {
                    _log($"image download error: {ex.Message}");
                    _browser.Reload(false);
                    continue;
                }
                
                stream.Seek(0, SeekOrigin.Begin);

                return stream;
                //_log("open image..");
                //string captchaPath = Path.Combine(_localPath, CaptchaFilename);
                //Process process = Process.Start(new ProcessStartInfo(captchaPath)
                //{
                //    UseShellExecute = true
                //});
            }
        }

        public async Task<bool> ResolveCaptcha(string userInput)
        {
            IBrowserHost host = _browser.GetBrowserHost();
            
            _log("paste user input");

            Coordinates coordinates = new Coordinates(0, 0);
            Coordinates inputCoordinates = await _getCoordinates(".Textinput-Control");
            await _mouseMove(host, coordinates, inputCoordinates);
            await _click(host, inputCoordinates);
            await _makeScreenshot("inputFocus");

            await Task.Delay(300);

            await _typeText(host, userInput);
            await Task.Delay(300);
            await _makeScreenshot("textTyped");

            Coordinates submitCoordinates = await _getCoordinates(".CaptchaButton_view_action");
            await _mouseMove(host, inputCoordinates, submitCoordinates);
            await _click(host, submitCoordinates);
            await _makeScreenshot("clickSubmit");

            await _waitPageLoading();

            bool isCaptchaResolved = !_browser.Address.Contains("captcha");

            if (!isCaptchaResolved) {
                _log("captcha failed, try again");
                return false;
            }

            await _makeScreenshot("success");
            _log("captcha successfully resolved!");

            return true;
        }

        private async Task provokeCaptchaPage()
        {
            int ticks = 0;
            while (true)
            {
                ticks++;

                await _browser.LoadUrlAsync("https://translate.yandex.ru/?source_lang=ru&target_lang=en&text=привет+" + ticks).ConfigureAwait(false);

                bool captchaRedirect = _browser.Address.Contains("captcha");

                if (captchaRedirect) break;
            }
        }

        private async Task _makeScreenshot(string name)
        {
            if (!_dbg) return;

            string filename = $"screenshot{_screenshotCounter}_{name}.png";

            byte[] bytes = await _browser.CaptureScreenshotAsync();

            string path = Path.Combine(_screenshotDirName, filename);
            File.WriteAllBytes(path, bytes);

            _log("screenshot made " + filename);
            _screenshotCounter++;
        }

        private async Task _waitPageLoading()
        {
            Console.WriteLine("wait for page loading");

            await _browser.WaitForNavigationAsync();

            Console.WriteLine("page loaded");
        }

        private async Task _waitElement(string selector)
        {
            JavascriptResponse jsRes;
            do
            {
                await Task.Delay(500);
                jsRes = await _browser.EvaluateScriptAsync($"document.querySelectorAll('{selector}').length");
            }
            while ((jsRes.Result as int?) == null || (jsRes.Result as int?) == 0);
        }

        private async Task<bool> _isElementExist(string selector)
        {
            JavascriptResponse jsRes = await _browser.EvaluateScriptAsync($"document.querySelectorAll('{selector}').length");
            
            return (jsRes.Result as int?) != null && (jsRes.Result as int?) > 0;
        }

        private async Task<bool> _isChromeErrorPage()
        {
            JavascriptResponse jsRes = await _browser.EvaluateScriptAsync($"location.href");
            
            return jsRes?.Result != null && (jsRes.Result as string).Contains("chrome-error");
        }

        private void _onAddressChanged(object sender, AddressChangedEventArgs e)
        {
            if (!_dbg) return;
            _log("address changed: " + e.Address);
        }

        private void _onLoadError(object sender, LoadErrorEventArgs e)
        {
            _log($"load error: ({e.ErrorCode}) {e.ErrorText}");
        }

        private MemoryStream _downloadUrl(string url)
        {
            var resultStream = new MemoryStream();
            
            _log(url + " downloading..");

            using (WebClient client = new WebClient())
            {
                using (Stream stream = client.OpenRead(url))
                {
                    stream.CopyTo(resultStream);
                    //using (FileStream fileStream = File.Create(filename))
                    //{
                    //    stream.CopyTo(fileStream);
                    //}
                }
            }
            
            _log("download complete");

            return resultStream;
        }

        private async Task _mouseMove(IBrowserHost host, Coordinates from, Coordinates to, int durationMs = 500, int steps = 50)
        {
            if (steps == 0) steps = 1;
            if (durationMs == 0) durationMs = 1;

            int stepMs = durationMs / steps;
            if (stepMs == 0) stepMs = 1;

            int xDelta = (to.X - from.X) / steps;
            int yDelta = (to.Y - from.Y) / steps;

            host.SendMouseMoveEvent(from.X, from.Y, false, CefEventFlags.None);
            if (xDelta == 0 && yDelta == 0) return;

            await Task.Delay(stepMs);
            Coordinates current = new Coordinates(from);
            for (int i = 0; i < steps; i++)
            {
                current.Change(xDelta, yDelta);
                host.SendMouseMoveEvent(current.X, current.Y, false, CefEventFlags.None);
                await Task.Delay(stepMs);
            }

            host.SendMouseMoveEvent(to.X, to.Y, false, CefEventFlags.None);
        }

        private async Task _click(IBrowserHost host, Coordinates point, MouseButtonType mouseButton = MouseButtonType.Left)
        {
            host.SendMouseClickEvent(point.X, point.Y, mouseButton, false, 1, CefEventFlags.None);
            await Task.Delay(100);
            host.SendMouseClickEvent(point.X, point.Y, mouseButton, true, 1, CefEventFlags.None);
        }

        private async Task<Coordinates> _getCoordinates(string selector)
        {
            await _browser.EvaluateScriptAsync($"var el = document.querySelector('{selector}');");
            JavascriptResponse jsRes = await _browser.EvaluateScriptAsync("var rect = el.getBoundingClientRect(); ({ x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 });");

            if (!jsRes.Success) return new Coordinates(0, 0);

            int x = (int)((jsRes.Result as dynamic)?.x ?? 0);
            int y = (int)((jsRes.Result as dynamic)?.y ?? 0);
            var res = new Coordinates(x, y);

            return res;
        }

        private async Task _typeText(IBrowserHost host, string text, int intervalMs = 100)
        {
            string loweredText = text.ToLowerInvariant();
            foreach (char c in loweredText)
            {
                await Task.Delay(intervalMs);

                KeyEvent keyDownEvent = _getKey(c, KeyEventType.KeyDown);
                host.SendKeyEvent(keyDownEvent);

                await Task.Delay(intervalMs);

                KeyEvent keyUpEvent = _getKey(c, KeyEventType.KeyUp);
                host.SendKeyEvent(keyUpEvent);
                KeyEvent charEvent = _getKey(c, KeyEventType.Char);
                host.SendKeyEvent(charEvent);
            }
        }

        private KeyEvent _getKey(char c, KeyEventType type)
        {
            var res = new KeyEvent
            {
                Modifiers = CefEventFlags.None,
                WindowsKeyCode = c,
                FocusOnEditableField = true,
                IsSystemKey = false,
                Type = type
            };

            return res;
        }

        private void _log(string msg)
        {
            var dt = DateTime.Now;
            Console.WriteLine($"{dt:dd.MM.yyyy_HH:mm:ss}: {msg}");
        }
    }
}
