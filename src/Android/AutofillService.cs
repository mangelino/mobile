using System;
using System.Collections.Generic;
using System.Linq;
using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views.Accessibility;
using Bit.App.Abstractions;
using XLabs.Ioc;
using Bit.App.Resources;

namespace Bit.Android
{
    [Service(Permission = global::Android.Manifest.Permission.BindAccessibilityService, Label = "bitwarden")]
    [IntentFilter(new string[] { "android.accessibilityservice.AccessibilityService" })]
    [MetaData("android.accessibilityservice", Resource = "@xml/accessibilityservice")]
    public class AutofillService : AccessibilityService
    {
        private NotificationChannel _notificationChannel;

        private const int AutoFillNotificationId = 34573;
        private const string SystemUiPackage = "com.android.systemui";
        private const string BitwardenPackage = "com.x8bit.bitwarden";
        private const string BitwardenWebsite = "bitwarden.com";

        private static Dictionary<string, Browser> SupportedBrowsers => new List<Browser>
        {
            new Browser("com.android.chrome", "url_bar"),
            new Browser("com.chrome.beta", "url_bar"),
            new Browser("com.android.browser", "url"),
            new Browser("com.brave.browser", "url_bar"),
            new Browser("com.opera.browser", "url_field"),
            new Browser("com.opera.browser.beta", "url_field"),
            new Browser("com.opera.mini.native", "url_field"),
            new Browser("com.chrome.dev", "url_bar"),
            new Browser("com.chrome.canary", "url_bar"),
            new Browser("com.google.android.apps.chrome", "url_bar"),
            new Browser("com.google.android.apps.chrome_dev", "url_bar"),
            new Browser("org.codeaurora.swe.browser", "url_bar"),
            new Browser("org.iron.srware", "url_bar"),
            new Browser("com.sec.android.app.sbrowser", "location_bar_edit_text"),
            new Browser("com.sec.android.app.sbrowser.beta", "location_bar_edit_text"),
            new Browser("com.yandex.browser", "bro_omnibar_address_title_text",
                (s) => s.Split(new char[]{' ', ' '}).FirstOrDefault()), // 0 = Regular Space, 1 = No-break space (00A0)
            new Browser("org.mozilla.firefox", "url_bar_title"),
            new Browser("org.mozilla.firefox_beta", "url_bar_title"),
            new Browser("org.mozilla.focus", "display_url"),
            new Browser("org.mozilla.klar", "display_url"),
            new Browser("com.ghostery.android.ghostery", "search_field"),
            new Browser("org.adblockplus.browser", "url_bar_title"),
            new Browser("com.htc.sense.browser", "title"),
            new Browser("com.amazon.cloud9", "url"),
            new Browser("mobi.mgeek.TunnyBrowser", "title"),
            new Browser("com.nubelacorp.javelin", "enterUrl"),
            new Browser("com.jerky.browser2", "enterUrl"),
            new Browser("com.mx.browser", "address_editor_with_progress"),
            new Browser("com.mx.browser.tablet", "address_editor_with_progress"),
            new Browser("com.linkbubble.playstore", "url_text"),
            new Browser("com.ksmobile.cb", "address_bar_edit_text"),
            new Browser("acr.browser.lightning", "search"),
            new Browser("acr.browser.barebones", "search"),
            new Browser("com.microsoft.emmx", "url_bar"),
            new Browser("com.duckduckgo.mobile.android", "omnibarTextInput")
        }.ToDictionary(n => n.PackageName);

        // Known packages to skip
        private static HashSet<string> FilteredPackageNames => new HashSet<string>
        {
            SystemUiPackage,
            "com.google.android.googlequicksearchbox",
            "com.google.android.apps.nexuslauncher",
            "com.google.android.launcher",
            "com.computer.desktop.ui.launcher",
            "com.launcher.notelauncher",
            "com.anddoes.launcher",
            "com.actionlauncher.playstore",
            "ch.deletescape.lawnchair.plah",
            "com.microsoft.launcher",
            "com.teslacoilsw.launcher",
            "com.teslacoilsw.launcher.prime",
            "is.shortcut",
            "me.craftsapp.nlauncher",
            "com.ss.squarehome2"
        };

        private readonly IAppSettingsService _appSettings;
        private long _lastNotificationTime = 0;
        private string _lastNotificationUri = null;
        private HashSet<string> _launcherPackageNames = null;
        private DateTime? _lastLauncherSetBuilt = null;
        private TimeSpan _rebuildLauncherSpan = TimeSpan.FromHours(1);

        public AutofillService()
        {
            _appSettings = Resolver.Resolve<IAppSettingsService>();
        }

        public override void OnAccessibilityEvent(AccessibilityEvent e)
        {
            var powerManager = (PowerManager)GetSystemService(PowerService);
            if(Build.VERSION.SdkInt > BuildVersionCodes.KitkatWatch && !powerManager.IsInteractive)
            {
                return;
            }
            else if(Build.VERSION.SdkInt < BuildVersionCodes.Lollipop && !powerManager.IsScreenOn)
            {
                return;
            }

            try
            {
                if(SkipPackage(e?.PackageName))
                {
                    return;
                }

                var root = RootInActiveWindow;
                if(root == null || root.PackageName != e.PackageName)
                {
                    return;
                }

                //var testNodes = GetWindowNodes(root, e, n => n.ViewIdResourceName != null && n.Text != null, false);
                //var testNodesData = testNodes.Select(n => new { id = n.ViewIdResourceName, text = n.Text });
                //testNodes.Dispose();

                var notificationManager = (NotificationManager)GetSystemService(NotificationService);
                var cancelNotification = true;

                LogInfo(e.PackageName + " fires event " + e.EventType);
                switch(e.EventType)
                {
                    case EventTypes.ViewFocused:
                        if(e.Source == null || !e.Source.Password || !_appSettings.AutofillPasswordField)
                        {
                            break;
                        }

                        if(e.PackageName == BitwardenPackage)
                        {
                            CancelNotification(notificationManager);
                            break;
                        }

                        if(ScanAndAutofill(root, e, notificationManager, cancelNotification))
                        {
                            CancelNotification(notificationManager);
                        }
                        break;
                    case EventTypes.WindowContentChanged:
                    case EventTypes.WindowStateChanged:
                        if(_appSettings.AutofillPasswordField && e.Source.Password)
                        {
                            break;
                        }
                        else if(_appSettings.AutofillPasswordField && AutofillActivity.LastCredentials == null)
                        {
                            LogInfo("1");
                            if(string.IsNullOrWhiteSpace(_lastNotificationUri))
                            {
                                LogInfo("2");
                                CancelNotification(notificationManager);
                                break;
                            }

                            LogInfo("3");
                            var uri = GetUri(root);
                            LogInfo("4");
                            if(uri != _lastNotificationUri)
                            {
                                LogInfo("5");
                                CancelNotification(notificationManager);
                            }
                            else if(uri.StartsWith(App.Constants.AndroidAppProtocol))
                            {
                                LogInfo("6");
                                CancelNotification(notificationManager, 30000);
                            }

                            break;
                        }

                        LogInfo("7");
                        if(e.PackageName == BitwardenPackage)
                        {
                            LogInfo("8");
                            CancelNotification(notificationManager);
                            break;
                        }

                        LogInfo("9");
                        if(_appSettings.AutofillPersistNotification)
                        {
                            var uri = GetUri(root);
                            if(uri != null && !uri.Contains(BitwardenWebsite))
                            {
                                var needToFill = NeedToAutofill(AutofillActivity.LastCredentials, uri);
                                if(needToFill)
                                {
                                    var passwordNodes = GetWindowNodes(root, e, n => n.Password, false);
                                    needToFill = passwordNodes.Any();
                                    if(needToFill)
                                    {
                                        var allEditTexts = GetWindowNodes(root, e, n => EditText(n), false);
                                        var usernameEditText = allEditTexts.TakeWhile(n => !n.Password).LastOrDefault();
                                        FillCredentials(usernameEditText, passwordNodes);

                                        allEditTexts.Dispose();
                                        usernameEditText.Dispose();
                                    }
                                    passwordNodes.Dispose();
                                }

                                if(!needToFill)
                                {
                                    NotifyToAutofill(uri, notificationManager);
                                    cancelNotification = false;
                                }
                            }

                            AutofillActivity.LastCredentials = null;
                        }
                        else
                        {
                            LogInfo("10");
                            cancelNotification = ScanAndAutofill(root, e, notificationManager, cancelNotification);
                        }

                        if(cancelNotification)
                        {
                            LogInfo("11");
                            CancelNotification(notificationManager);
                        }
                        break;
                    default:
                        break;
                }

                LogInfo("12");
                notificationManager?.Dispose();
                root.Dispose();
                e.Dispose();
            }
            // Suppress exceptions so that service doesn't crash
            catch(Exception ex)
            {
                LogError(ex.Message);
                throw ex;
            }
        }

        private void LogInfo(string msg)
        {
            global::Android.Util.Log.Info("bw_access", msg);
        }

        private void LogError(string msg)
        {
            global::Android.Util.Log.Error("bw_access", msg);
        }

        public override void OnInterrupt()
        {

        }

        public bool ScanAndAutofill(AccessibilityNodeInfo root, AccessibilityEvent e,
            NotificationManager notificationManager, bool cancelNotification)
        {
            LogInfo("13");
            var passwordNodes = GetWindowNodes(root, e, n => n.Password, false);
            LogInfo("500000000000");
            if(passwordNodes.Count > 0)
            {
                var uri = GetUri(root);
                if(uri != null && !uri.Contains(BitwardenWebsite))
                {
                    if(NeedToAutofill(AutofillActivity.LastCredentials, uri))
                    {
                        LogInfo("14");
                        var allEditTexts = GetWindowNodes(root, e, n => EditText(n), false);
                        var usernameEditText = allEditTexts.TakeWhile(n => !n.Password).LastOrDefault();
                        FillCredentials(usernameEditText, passwordNodes);

                        LogInfo("15");
                        allEditTexts.Dispose();
                        usernameEditText.Dispose();
                    }
                    else
                    {
                        LogInfo("16");
                        NotifyToAutofill(uri, notificationManager);
                        cancelNotification = false;
                    }
                }

                AutofillActivity.LastCredentials = null;
            }
            else if(AutofillActivity.LastCredentials != null)
            {
                LogInfo("17");
                System.Threading.Tasks.Task.Run(async () =>
                {
                    LogInfo("18");
                    await System.Threading.Tasks.Task.Delay(1000);
                    LogInfo("19");
                    AutofillActivity.LastCredentials = null;
                });
            }

            LogInfo("20");
            passwordNodes.Dispose();
            return cancelNotification;
        }

        public void CancelNotification(NotificationManager notificationManager, long limit = 250)
        {
            if(Java.Lang.JavaSystem.CurrentTimeMillis() - _lastNotificationTime < limit)
            {
                return;
            }

            _lastNotificationUri = null;
            notificationManager?.Cancel(AutoFillNotificationId);
        }

        private string GetUri(AccessibilityNodeInfo root)
        {
            var uri = string.Concat(App.Constants.AndroidAppProtocol, root.PackageName);
            if(SupportedBrowsers.ContainsKey(root.PackageName))
            {
                var addressNode = root.FindAccessibilityNodeInfosByViewId(
                    $"{root.PackageName}:id/{SupportedBrowsers[root.PackageName].UriViewId}").FirstOrDefault();
                if(addressNode != null)
                {
                    uri = ExtractUri(uri, addressNode, SupportedBrowsers[root.PackageName]);
                    addressNode.Dispose();
                }
            }

            return uri;
        }

        private string ExtractUri(string uri, AccessibilityNodeInfo addressNode, Browser browser)
        {
            if(addressNode?.Text != null)
            {
                uri = browser.GetUriFunction(addressNode.Text).Trim();
                if(uri != null && uri.Contains("."))
                {
                    if(!uri.Contains("://") && !uri.Contains(" "))
                    {
                        uri = string.Concat("http://", uri);
                    }
                    else if(Build.VERSION.SdkInt <= BuildVersionCodes.KitkatWatch)
                    {
                        var parts = uri.Split(new string[] { ". " }, StringSplitOptions.None);
                        if(parts.Length > 1)
                        {
                            var urlPart = parts.FirstOrDefault(p => p.StartsWith("http"));
                            if(urlPart != null)
                            {
                                uri = urlPart.Trim();
                            }
                        }
                    }
                }
            }

            return uri;
        }

        /// <summary>
        /// Check to make sure it is ok to autofill still on the current screen
        /// </summary>
        private bool NeedToAutofill(AutofillCredentials creds, string currentUriString)
        {
            if(creds == null)
            {
                return false;
            }

            Uri lastUri, currentUri;
            if(Uri.TryCreate(creds.LastUri, UriKind.Absolute, out lastUri) &&
                Uri.TryCreate(currentUriString, UriKind.Absolute, out currentUri) &&
                lastUri.Host == currentUri.Host)
            {
                return true;
            }

            return false;
        }

        private static bool EditText(AccessibilityNodeInfo n)
        {
            return n?.ClassName?.Contains("EditText") ?? false;
        }

        private void NotifyToAutofill(string uri, NotificationManager notificationManager)
        {
            if(notificationManager == null || string.IsNullOrWhiteSpace(uri))
            {
                return;
            }

            var now = Java.Lang.JavaSystem.CurrentTimeMillis();
            var intent = new Intent(this, typeof(AutofillActivity));
            intent.PutExtra("uri", uri);
            intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);
            var pendingIntent = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.UpdateCurrent);

            var notificationContent = Build.VERSION.SdkInt > BuildVersionCodes.KitkatWatch ?
                AppResources.BitwardenAutofillServiceNotificationContent :
                AppResources.BitwardenAutofillServiceNotificationContentOld;

            var builder = new Notification.Builder(this);
            builder.SetSmallIcon(Resource.Drawable.notification_sm)
                   .SetContentTitle(AppResources.BitwardenAutofillService)
                   .SetContentText(notificationContent)
                   .SetTicker(notificationContent)
                   .SetWhen(now)
                   .SetContentIntent(pendingIntent);

            if(Build.VERSION.SdkInt > BuildVersionCodes.KitkatWatch)
            {
                builder.SetVisibility(NotificationVisibility.Secret)
                    .SetColor(global::Android.Support.V4.Content.ContextCompat.GetColor(ApplicationContext,
                        Resource.Color.primary));
            }

            if(Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                if(_notificationChannel == null)
                {
                    _notificationChannel = new NotificationChannel("bitwarden_autofill_service",
                        AppResources.AutofillService, NotificationImportance.Low);
                    notificationManager.CreateNotificationChannel(_notificationChannel);
                }
                builder.SetChannelId(_notificationChannel.Id);
            }

            if(/*Build.VERSION.SdkInt <= BuildVersionCodes.N && */_appSettings.AutofillPersistNotification)
            {
                builder.SetPriority(-2);
            }

            _lastNotificationTime = now;
            _lastNotificationUri = uri;
            notificationManager.Notify(AutoFillNotificationId, builder.Build());

            builder.Dispose();
        }

        private void FillCredentials(AccessibilityNodeInfo usernameNode, IEnumerable<AccessibilityNodeInfo> passwordNodes)
        {
            FillEditText(usernameNode, AutofillActivity.LastCredentials?.Username);
            foreach(var n in passwordNodes)
            {
                FillEditText(n, AutofillActivity.LastCredentials?.Password);
            }
        }

        private static void FillEditText(AccessibilityNodeInfo editTextNode, string value)
        {
            if(editTextNode == null || value == null)
            {
                return;
            }

            var bundle = new Bundle();
            bundle.PutString(AccessibilityNodeInfo.ActionArgumentSetTextCharsequence, value);
            editTextNode.PerformAction(global::Android.Views.Accessibility.Action.SetText, bundle);
        }

        private NodeList GetWindowNodes(AccessibilityNodeInfo n, AccessibilityEvent e,
            Func<AccessibilityNodeInfo, bool> condition, bool disposeIfUnused, NodeList nodes = null, int j = 0)
        {
            LogInfo("51");
            if(nodes == null)
            {
                nodes = new NodeList();
            }

            if(n != null)
            {
                LogInfo("52: " + n.ChildCount + ", " + n.WindowId + ", " + n.ViewIdResourceName + ", " + n.Password + ", " + j);
                var dispose = disposeIfUnused;
                if(n.WindowId == e.WindowId && !(n.ViewIdResourceName?.StartsWith(SystemUiPackage) ?? false) && condition(n))
                {
                    LogInfo("53");
                    dispose = false;
                    nodes.Add(n);
                    LogInfo("53.1: " + nodes.Count);
                }

                LogInfo("54: " + n.ChildCount);
                for(var i = 0; i < n.ChildCount; i++)
                {
                    LogInfo("55.1");
                    var c = n.GetChild(i);
                    LogInfo("55.2");
                    GetWindowNodes(c, e, condition, true, nodes, j++);
                    LogInfo("55.3: " + i + ", " + n.ChildCount + ", " + n.WindowId + ", " + n.ViewIdResourceName + ", " + n.Password + ", " + j);
                }

                if(dispose)
                {
                    LogInfo("56");
                    n.Dispose();
                }
            }

            LogInfo("57: " + nodes.Count);
            return nodes;
        }

        private bool SkipPackage(string eventPackageName)
        {
            if(string.IsNullOrWhiteSpace(eventPackageName) || FilteredPackageNames.Contains(eventPackageName)
                || eventPackageName.Contains("launcher"))
            {
                return true;
            }

            if(_launcherPackageNames == null || _lastLauncherSetBuilt == null ||
                (DateTime.Now - _lastLauncherSetBuilt.Value) > _rebuildLauncherSpan)
            {
                // refresh launcher list every now and then
                _lastLauncherSetBuilt = DateTime.Now;
                var intent = new Intent(Intent.ActionMain);
                intent.AddCategory(Intent.CategoryHome);
                var resolveInfo = PackageManager.QueryIntentActivities(intent, 0);
                _launcherPackageNames = resolveInfo.Select(ri => ri.ActivityInfo.PackageName).ToHashSet();
            }

            return _launcherPackageNames.Contains(eventPackageName);
        }

        public class Browser
        {
            public Browser(string packageName, string uriViewId)
            {
                PackageName = packageName;
                UriViewId = uriViewId;
            }

            public Browser(string packageName, string uriViewId, Func<string, string> getUriFunction)
                : this(packageName, uriViewId)
            {
                GetUriFunction = getUriFunction;
            }

            public string PackageName { get; set; }
            public string UriViewId { get; set; }
            public Func<string, string> GetUriFunction { get; set; } = (s) => s;
        }

        public class NodeList : List<AccessibilityNodeInfo>, IDisposable
        {
            public void Dispose()
            {
                foreach(var item in this)
                {
                    item.Dispose();
                }
            }
        }
    }
}
