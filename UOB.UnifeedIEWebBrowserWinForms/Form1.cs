#define ACCEPT
namespace UOL.UnifeedIEWebBrowserWinForms
{
	using System;
	using System.Collections.Generic;
	using System.Collections.Specialized;
	using System.IO;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;
	using System.Web;
	using System.Windows.Forms;
	using Microsoft.Toolkit.Win32.UI.Controls.Interop.WinRT;
	using Microsoft.Web.WebView2.Core;
	using Newtonsoft.Json;
	using UOL.SharedCode.Authentication;
	using static System.Net.Mime.MediaTypeNames;
	using static System.Windows.Forms.LinkLabel;

	public partial class Form1 : Form
	{
		public const string ClientId = "2BA_DEMOAPPS_PKCE";
#if LOCAL
		public const string AuthorizeBaseUrl = "https://authorize.alpha.2ba.nl";
		public const string UnifeedBaseUrl = "https://localhost:44337/";
		public const string ApiBaseUrlNew = "https://apix.alpha.2ba.nl";
#elif ALPHA
		public const string AuthorizeBaseUrl = "https://authorize.alpha.2ba.nl";
		public const string UnifeedBaseUrl = "https://uol-unifeed.alpha.2ba.nl";
		public const string ApiBaseUrlNew = "https://apix.alpha.2ba.nl";
#elif ACCEPT
		public const string AuthorizeBaseUrl = "https://authorize.accept.2ba.nl";
		public const string UnifeedBaseUrl = "https://uol-unifeed.accept.2ba.nl";
		public const string ApiBaseUrlNew = "https://apix.accept.2ba.nl";
#else
		public const string AuthorizeBaseUrl = "https://authorize.2ba.nl";
		public const string UnifeedBaseUrl = "https://uol-unifeed.2ba.nl";
		public const string ApiBaseUrlNew = "https://apix.2ba.nl";
#endif
		public const string UnifeedSchemeName = "nl.2ba.uol";
		public static bool EmbeddedAuth = true;
		public static bool WebView2Available = false;
		public static readonly string AuthorizeUrl = $"{AuthorizeBaseUrl}/OAuth/Authorize";
		public static readonly string AuthorizeTokenUrl = $"{AuthorizeBaseUrl}/OAuth/Token";
		public static readonly string AuthorizeListenerAddress = EmbeddedAuth ? $"{UnifeedSchemeName}://" : $"http://localhost:43215/"; // Must end with slash
		public static readonly string AuthorizeHookAuthority = "tokenreceiver";
		public static readonly string AuthorizeHookUrl = $"{AuthorizeListenerAddress}{AuthorizeHookAuthority}"; // = Redirect_uri as configured for client
		public static readonly string UnifeedHookUrl = $"{UnifeedSchemeName}://request";

		private OAuthToken _currentToken = null;
		private PKCECode _pkcetemp = null;
		private string _lastRetrievedJson = null;
		private BBA.UnifeedApi.InterfaceModel _lastRetrievedObject = null;
		private readonly Authentication authService = null;

		public Form1()
		{
			InitializeComponent();

			Log($"Application starting");

			authService = new Authentication(new AuthenticationConfig()
			{
				AuthorizeUrl = AuthorizeUrl,
				AuthorizeHookUrl = AuthorizeHookUrl,
				AuthorizeListenerAddress = AuthorizeListenerAddress,
				AuthorizeTokenUrl = AuthorizeTokenUrl,
				ClientId = ClientId,
				RequestedScope = "unifeed openid offline_access apix",
			});

			try
			{
				// Check if WebView2 is available, throws WebView2RuntimeNotFoundException when not found
				Log($"Check if WebView2 is available");
				var webViewVersionAvail = CoreWebView2Environment.GetAvailableBrowserVersionString();
				Log($"WebView2 (Edge) is available with version {webViewVersionAvail}! ");

				WebView2Available = true;
				// Enable WebView2 form control
				browser.Enabled = true;
				browser.Visible = true;
				// Disable legacy WebViewCompatible form control
				webView1.Enabled = false;
				webView1.Visible = false;

				browser.EnsureCoreWebView2Async().GetAwaiter().OnCompleted(() =>
				{
					browser.CoreWebView2.Settings.IsStatusBarEnabled = true;
					browser.CoreWebView2.NavigationStarting += Browser_NavigationStarting;
					browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
				});
			}
			catch (WebView2RuntimeNotFoundException)
			{
				Log($"WebView2 is NOT available!");
				// Fallback to legacy WebView form control
				WebView2Available = false;
				// Disable WebView2 form control
				browser.Enabled = false;
				browser.Visible = false;

				if (webView1 != null)
				{
					// Enable legacy WebViewCompatible form control
					webView1.Enabled = true;
					webView1.Visible = true;
					if (webView1.Controls[0] is Microsoft.Toolkit.Forms.UI.Controls.WebView webView)
					{
						webView.UnsupportedUriSchemeIdentified += WebView_UnsupportedUriSchemeIdentified;
						webView.NewWindowRequested += WebView_NewWindowRequested;
						webView.NavigationStarting += WebView_NavigationStarting;
					}
					else if (browser.Controls[0] is WebBrowser webBrowser)
					{
						webBrowser.ScriptErrorsSuppressed = false;
						webView1.NavigationStarting += WebView_NavigationStarting;
					}
				}
			}
		}

		private async void Form1_Load(object sender, EventArgs e)
		{
			var browserType = browser.ToString();
			if (!WebView2Available)
			{
				browserType = webView1.ToString();
			}
			Log($"Form loaded. Webbrowser type: {browserType}");
			Log($"Starting authentication");
			await Authenticate();
		}

		private async Task Authenticate()
		{
			_currentToken = TokenRepository.GetToken();

			if (_currentToken != null && _currentToken.Environment == AuthorizeBaseUrl && _currentToken.IsExpired)
			{
				try
				{
					RefreshToken();
					await AuthenticationCompleteAsync();
				}
				catch (Exception)
				{
					TokenRepository.StoreToken(null);
					await Authenticate();
				}
			}
			else if (_currentToken == null || _currentToken.Environment != AuthorizeBaseUrl)
			{
				if (EmbeddedAuth)
				{
					(string url, PKCECode pkcecodes) x = authService.BuildAuthorizationCodeUrl();
					_pkcetemp = x.pkcecodes;
					await Navigate(x.url);
				}
				else
				{
					_currentToken = await authService.Authenticate();
					_currentToken.Environment = AuthorizeBaseUrl;
					TokenRepository.StoreToken(_currentToken);
					await AuthenticationCompleteAsync();
				}
			}
			else // Token set and not expired
			{
				await AuthenticationCompleteAsync();
			}
		}

		private async Task AuthenticationCompleteAsync()
		{
			Log($"Authentication complete, starting Unifeed");
			await StartUnifeedAsync();
		}

		private async Task StartUnifeedAsync(long? interfaceObjectId = null)
		{
			var accessToken = _currentToken?.AccessToken;

			var url = SharedCode.Web.HttpExtensions.Build(UnifeedBaseUrl, new NameValueCollection()
			{
				{ "accessToken", accessToken },
				{ "interface", (32 | 64).ToString() },
				{ "interfaceObjectId", interfaceObjectId?.ToString() },
				{ "interfaceType", "JSONGET" },
				{ "interfaceName", "DemoApp" },
				{ "uobApplication", "DemoCad" },
				{ "uobApplicationVersion", "2022" },
				{ "hookUrl", UnifeedHookUrl },

			}).ToString();

			Log($"Starting Unifeed with url: {url}");

			await Navigate(url);
			return;
		}

		/// <summary>
		/// NavigationStarting event handler for WebView2 browser
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private async void Browser_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
		{
			// Log($"WebView_NavigationStarting: {e.Uri}");
			var uri = new Uri(e.Uri);
			if (uri.Scheme == UnifeedSchemeName)
			{
				e.Cancel = true;
				Log($"Interfaced (through WebView2.NavigationStarting)! {e.Uri}");

				await UnifeedInterfaced(uri);
			}
			else if (uri.AbsolutePath.EndsWith("account/ForgotPasswordConfirmation", StringComparison.OrdinalIgnoreCase))
			{
				e.Cancel = true;
				await Authenticate();
				BeginInvoke(new Action(() =>
				{
					MessageBox.Show("An email is on it's way to your mailbox with instructions on how to reset your password.", "Password reset requested", MessageBoxButtons.OK);
				}));
			}
		}

		/// <summary>
		/// NewWindowRequested event handler for WebView2 browser
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void Browser_NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
		{

			Log($"Browser_NewWindowRequested: {e.Uri}");

			if (!e.Uri.ToString().Contains("viewer3d"))
			{
				SharedCode.Web.SystemBrowser.OpenBrowser(e.Uri.ToString());
			}
		}

		/// <summary>
		/// UnsupportedUriSchemeIdentified event handler for legacy WebView browser
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private async void WebView_UnsupportedUriSchemeIdentified(object sender, WebViewControlUnsupportedUriSchemeIdentifiedEventArgs e)
		{
			// Log($"WebView_UnsupportedUriSchemeIdentified: {e.Uri}");
			if (e.Uri.Scheme == UnifeedSchemeName)
			{
				e.Handled = true;
				Log($"Interfaced (through WebView.UnsupportedUriSchemeIdentified)! {e.Uri}");

				await UnifeedInterfaced(e.Uri);
			}
		}

		/// <summary>
		/// NavigationStarting event handler for legacy WebView browser
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private async void WebView_NavigationStarting(object sender, WebViewControlNavigationStartingEventArgs e)
		{
			// Log($"WebView_NavigationStarting: {e.Uri}");
			if (e.Uri.Scheme == UnifeedSchemeName)
			{
				e.Cancel = true;
				Log($"Interfaced (through WebView.NavigationStarting)! {e.Uri}");

				await UnifeedInterfaced(e.Uri);
			}
			else if (e.Uri.AbsolutePath.EndsWith("account/ForgotPasswordConfirmation", StringComparison.OrdinalIgnoreCase))
			{
				e.Cancel = true;
				await Authenticate();
				BeginInvoke(new Action(() =>
				{
					MessageBox.Show("An email is on it's way to your mailbox with instructions on how to reset your password.", "Password reset requested", MessageBoxButtons.OK);
				}));
			}
		}

		/// <summary>
		/// NewWindowRequested event handler for legacy WebView browser
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void WebView_NewWindowRequested(object sender, WebViewControlNewWindowRequestedEventArgs e)
		{
			Log($"WebView_NewWindowRequested: {e.Uri}");
			SharedCode.Web.SystemBrowser.OpenBrowser(e.Uri.ToString());
		}

		private async Task UnifeedInterfaced(Uri interfaceUrl)
		{
			var queryString = HttpUtility.ParseQueryString(interfaceUrl.Query);

			if (interfaceUrl.Authority == AuthorizeHookAuthority)
			{
				var code = queryString["code"];
				_currentToken = authService.RetrieveToken(code, _pkcetemp.CodeVerifier);
				_currentToken.Environment = AuthorizeBaseUrl;
				TokenRepository.StoreToken(_currentToken);
				await AuthenticationCompleteAsync();
				return;
			}

			// Get id from data
			var data = queryString["json"];

			var interfaceBaseInfo = JsonConvert.DeserializeAnonymousType(data, new { Type = (string)null });
			Log($"Retrieved interfacetype: {interfaceBaseInfo.Type}");

			if ("interface".Equals(interfaceBaseInfo.Type, StringComparison.OrdinalIgnoreCase))
			{
				UnifeedInterfaceInterface(data);
			}
			else if ("productselection".Equals(interfaceBaseInfo.Type, StringComparison.OrdinalIgnoreCase))
			{
				await UnifeedInterfaceProductSelection(data);
			}
			else if ("composition".Equals(interfaceBaseInfo.Type, StringComparison.OrdinalIgnoreCase))
			{
				await UnifeedInterfaceComposition(data);
			}

			// Restart situation
			if (_currentToken.IsExpired && !string.IsNullOrEmpty(_currentToken.RefreshToken))
			{
				// Refresh token. Normally this is not needed for every call, only when the token is expired.
				// Only possible when offline_access scope is honored
				RefreshToken();
			}

			await StartUnifeedAsync(); // browser.Refresh();
		}

		private void UnifeedInterfaceInterface(string data)
		{
			var interfaceInfo = JsonConvert.DeserializeAnonymousType(data, new { Id = 0L, Type = (string)null });
			Log($"Retrieved id for interface object: {interfaceInfo.Id}. Type: {interfaceInfo.Type}");

			if (interfaceInfo.Id == 0)
			{
				Log($"ERROR! Retrieved interface id = 0");
				return;
			}
			var url = SharedCode.Web.HttpExtensions.Build($"{ApiBaseUrlNew}/api/v1/unifeed/UobInterface/{interfaceInfo.Id}").ToString();

			Log($"Calling service url: {url}");

			_lastRetrievedJson = SharedCode.WebService.WebServiceHelper.GetJson(url, _currentToken.AccessToken);
			_lastRetrievedJson = Newtonsoft.Json.Linq.JToken.Parse(_lastRetrievedJson).ToString(Formatting.Indented);
			Log($"Retrieved interface object: {_lastRetrievedJson}");

			_lastRetrievedObject = JsonConvert.DeserializeObject<BBA.UnifeedApi.InterfaceModel>(_lastRetrievedJson);
			lblLastObject.Text = $"{_lastRetrievedObject.Product.ManufacturerGln.Value}/{_lastRetrievedObject.Product.ProductCode.Value} EC: {_lastRetrievedObject.Product.EtimClass.Code}/{_lastRetrievedObject.Product.EtimClass.Version} MC: {_lastRetrievedObject.Product.ModellingClass.Code}/{_lastRetrievedObject.Product.ModellingClass.Version}";
			btnStartWithLastObject.Enabled = true;
			btnDownloadJson.Enabled = true;
		}

		private async Task UnifeedInterfaceProductSelection(string data)
		{
			var interfaceInfo = JsonConvert.DeserializeAnonymousType(data, new { Id = 0, Type = (string)null, DisableFields = (ICollection<BBA.UnifeedApi.ProductAttribute>)null });
			Log($"Retrieved id for interface object: {interfaceInfo.Id}. Type: {interfaceInfo.Type}");

			// Build URL to call ProductSelection Service
			var url = SharedCode.Web.HttpExtensions.Build($"{ApiBaseUrlNew}/api/v1/unifeed/UobProduct/FromSelectionList").ToString();

			var selectionListRequest = new BBA.UnifeedApi.SelectionListParms()
			{
				SelectionListId = interfaceInfo.Id,
				DisableFields = interfaceInfo.DisableFields,
				Languagecode = BBA.UnifeedApi.Languagecode.NL
			};
			var selectionListRequestString = JsonConvert.SerializeObject(selectionListRequest);

			// Call the service
			Log($"Calling service url: {url}");
			var interfaceObjectJson = await SharedCode.WebService.WebServiceHelper.PostJson(url, _currentToken.AccessToken, selectionListRequestString);

			// Deserialize
			var productlist = JsonConvert.DeserializeObject<List<BBA.UnifeedApi.ProductModel2>>(interfaceObjectJson);
			Log($"Retrieved productselection object. Total products in list: {productlist.Count}");

			// Interface handling
			btnStartWithLastObject.Enabled = false;
			btnDownloadJson.Enabled = false;
		}

		private async Task UnifeedInterfaceComposition(string data)
		{
			var interfaceInfo = JsonConvert.DeserializeAnonymousType(data, new { Id = 0, Type = (string)null, DisableFields = (ICollection<BBA.UnifeedApi.ProductAttribute>)null });
			Log($"Retrieved id for interface object: {interfaceInfo.Id}. Type: {interfaceInfo.Type}");

			// Build URL to call ProductSelection Service
			var url = SharedCode.Web.HttpExtensions.Build($"{ApiBaseUrlNew}/api/v1/unifeed/UobProduct/FromComposition").ToString();

			var selectionListRequest = new BBA.UnifeedApi.CompositionParms()
			{
				CompositionId = interfaceInfo.Id,
				DisableFields = interfaceInfo.DisableFields,
				Languagecode = BBA.UnifeedApi.Languagecode.NL
			};
			var selectionListRequestString = JsonConvert.SerializeObject(selectionListRequest);

			// Call the service
			Log($"Calling service url: {url}");
			var interfaceObjectJson = await SharedCode.WebService.WebServiceHelper.PostJson(url, _currentToken.AccessToken, selectionListRequestString);

			// Deserialize
			var productlist = JsonConvert.DeserializeObject<List<BBA.UnifeedApi.ProductModel2>>(interfaceObjectJson);

			Log($"Retrieved composition object. Distinct products in list: {productlist.Count}. Total positions: {productlist.SelectMany(c => c.CompositionPositions).Count()}");
			var matrixrepresentation = BuildMatrixRepresentation(productlist);
			Log(matrixrepresentation);

			// Interface handling
			btnStartWithLastObject.Enabled = false;
			btnDownloadJson.Enabled = false;
		}

		private string BuildMatrixRepresentation(List<BBA.UnifeedApi.ProductModel2> productlist)
		{
			var data2 = productlist.SelectMany(c => c.CompositionPositions.Select(p => new { c.ProductCode, p.Level, p.Position, p.PositionSpan, p.Offset })).OrderBy(z => z.Level).OrderBy(l => l.Position);

			var ml = data2.Select(c => c.ProductCode).Distinct().Max(x => x.Value.Length);

			var str = new StringBuilder();
			str.AppendLine();
			foreach (var row in data2.Select(y => y.Level).Distinct().OrderBy(z => z))
			{
				if (row == 1)
				{
					foreach (var pos in data2.Select(y => y.Position).Distinct().OrderBy(z => z))
					{
						if (pos == 1)
						{
							str.Append($"[\\]");
						}
						var tt = $"{string.Concat(Enumerable.Repeat(" ", ml))}{pos}";
						str.Append($"[{tt.Substring(tt.Length - ml, ml)}]");
						;
					}
					str.AppendLine();
				}

				str.Append($"[{row}]");

				var spancntr = 0;
				foreach (var pos in data2.Select(y => y.Position).Distinct().OrderBy(z => z))
				{
					if (spancntr > 0)
					{
						spancntr--;
						continue;
					}
					var pc = data2.FirstOrDefault(x => x.Level == row && x.Position == pos)?.ProductCode.Value;
					var span = data2.FirstOrDefault(x => x.Level == row && x.Position == pos)?.PositionSpan ?? 1;
					var tt = $"{string.Concat(Enumerable.Repeat(string.Concat(Enumerable.Repeat(" ", ml)), span))}{pc}";
					var fl = ml;
					if (span > 1)
					{
						tt = string.Concat(Enumerable.Repeat("  ", span)) + tt;
						fl = ((span - 1) * 2) + span * ml;
						spancntr = span;
					}

					str.Append($"[{tt.Substring(tt.Length - fl, fl)}]");
				}

				str.AppendLine();
			}

			return str.ToString();
		}

		private void Log(string tekst)
		{
			logBox.Text += $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")} {tekst} {System.Environment.NewLine}";
			// System.Diagnostics.Debug.WriteLine($"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")} {tekst} {Environment.NewLine}");

			logBox.SelectionStart = logBox.Text.Length;
			logBox.ScrollToCaret();
		}

		private void RefreshToken()
		{
			Log($"Refreshing tokens. Old token retrieved: {_currentToken.TokenIssued.ToString("yyyyMMdd_HHmmss")}");
			var query = SharedCode.Web.HttpExtensions.BuildQuerystring(new NameValueCollection()
				{
					{ "client_id", ClientId },
					{ "grant_type", "refresh_token" },
					{ "refresh_token", _currentToken.RefreshToken },
				}).ToString();

			_currentToken = TokenService.RetrieveToken(AuthorizeTokenUrl, query);
			_currentToken.Environment = AuthorizeBaseUrl;
			TokenRepository.StoreToken(_currentToken);

			Log($"Refreshing tokens. New token retrieved: {_currentToken.TokenIssued.ToString("yyyyMMdd_HHmmss")}");
		}

		private void btnClearLog_Click(object sender, EventArgs e)
		{
			logBox.Text = string.Empty;
		}

		private async void btnStartWithLastObject_Click(object sender, EventArgs e)
		{
			if (_lastRetrievedObject == null)
			{
				btnStartWithLastObject.Enabled = false;
				btnDownloadJson.Enabled = false;
				return;
			}

			var interfaceObjectJson = JsonConvert.SerializeObject(_lastRetrievedObject);

			// Post the interfaceObject back
			//var postUrl = SharedCode.Web.HttpExtensions.Build($"{ApiBaseUrlOld}/json/UOB/Interface").ToString();
			var postUrl = SharedCode.Web.HttpExtensions.Build($"{ApiBaseUrlNew}/api/v1/unifeed/UobInterface").ToString();
			Log($"Posting object back to: {postUrl}");
			var output = await SharedCode.WebService.WebServiceHelper.PostJson(postUrl, _currentToken.AccessToken, interfaceObjectJson);

			Log($"Retrieved interface id: {output}");

			var lastInterfaceId = long.Parse(output);
			await StartUnifeedAsync(lastInterfaceId);
		}

		private async void tbnStartClear_Click(object sender, EventArgs e)
		{
			await StartUnifeedAsync();
		}

		private async void btnReset_Click(object sender, EventArgs e)
		{
			await Navigate(UnifeedBaseUrl);
			TokenRepository.StoreToken(null);
			await Authenticate();
		}

		private async Task Navigate(string url, bool ensureWv2Created = true)
		{
			if (WebView2Available)
			{
				await browser.EnsureCoreWebView2Async();
				browser.CoreWebView2.Navigate(url);
			}
			else
			{
				webView1.Navigate(url);
			}
		}

		private void btnDownload_Click(object sender, EventArgs e)
		{
			var accessToken = _currentToken?.AccessToken;

			var downloadUrl = SharedCode.Web.HttpExtensions.Build(UnifeedBaseUrl, new NameValueCollection()
			{
				{ "accessToken", accessToken },
				{ "interface", 32.ToString() },
				{ "interfaceType", "DOWNLOAD" },
				{ "interfaceName", "lokaal apparaat (download)" },
				{ "uobApplication", "Revit" },
				{ "uobApplicationVersion", "2018" },
			}).ToString();

			Log($"Link to initiate download: {downloadUrl}");

			SharedCode.Web.SystemBrowser.OpenBrowser(downloadUrl);
		}

		private void btnDownloadJson_Click(object sender, EventArgs e)
		{
			var myTempFile = Path.GetTempFileName() + ".json";
			using (var sw = new StreamWriter(myTempFile))
			{
				sw.WriteLine(_lastRetrievedJson);
			}

			var p = new System.Diagnostics.Process();

			p.StartInfo.FileName = myTempFile;
			p.StartInfo.UseShellExecute = true;
			p.Start();
		}
	}
}
