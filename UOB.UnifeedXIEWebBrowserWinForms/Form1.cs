﻿namespace UOL.UnifeedXIEWebBrowserWinForms
{
	using System;
	using System.Collections.Specialized;
	using System.Net;
	using System.Text;
	using System.Threading.Tasks;
	using System.Web;
	using System.Windows.Forms;
	using Newtonsoft.Json;

	public partial class Form1 : Form
	{
		public const string ClientId = "2BA_DEMOAPPS_PKCE";
		public const string AuthorizeBaseUrl = "https://uol-auth.2ba.nl";
		public const string UnifeedBaseUrl = "https://unifeed-uol.alpha.2ba.nl";
		public const string UnifeedSchemeName = "nl.2ba.uol";
		public static readonly string AuthorizeUrl = $"{AuthorizeBaseUrl}/OAuth/Authorize";
		public static readonly string AuthorizeTokenUrl = $"{AuthorizeBaseUrl}/OAuth/Token";
		public static readonly string AuthorizeListenerAddress = $"http://localhost:43215/"; // Must end with slash
		public static readonly string AuthorizeHookUrl = $"{AuthorizeListenerAddress}tokenreceiver"; // = Redirect_uri as configured for client
		public static readonly string UnifeedHookUrl = $"{UnifeedSchemeName}://request";
		public static readonly string UnifeedStartUrl = $"{UnifeedBaseUrl}/start";
		public static readonly string UnifeedApiUrl = $"{UnifeedBaseUrl}/api";

		private SharedCode.Authentication.OAuthToken _currentToken = null;

		public Form1()
		{
			InitializeComponent();
		}

		private async void Form1_Load(object sender, EventArgs e)
		{
			Log($"Form loaded, starting authenticate");

			var authService = new SharedCode.Authentication.Authentication(new SharedCode.Authentication.AuthenticationConfig()
			{
				AuthorizeUrl = AuthorizeUrl,
				AuthorizeHookUrl = AuthorizeHookUrl,
				AuthorizeListenerAddress = AuthorizeListenerAddress,
				AuthorizeTokenUrl = AuthorizeTokenUrl,
				ClientId = ClientId
			});

			_currentToken = await authService.Authenticate();

			Log($"Authentication complete, starting Unifeed");
			StartUnifeed();
		}

		private void StartUnifeed()
		{
			var accessToken = _currentToken.AccessToken;
			var url = SharedCode.Web.HttpExtensions.Build(UnifeedStartUrl, new NameValueCollection()
			{
				{ "accessToken", accessToken },
				{ "hookUrl", UnifeedHookUrl },
			}).ToString();

			var searchParms = new UnifeedObjects.SearchParms()
			{
				SearchString = "uob",
				From = 20,
				Size = 20,
				Languagecode = "NL",
				Filters = new System.Collections.Generic.List<UnifeedObjects.FilterModel>()
				{
					new UnifeedObjects.FilterModel()
					{
						Code = UnifeedObjects.Filtercode.ModellingClass,
						Values = new System.Collections.Generic.List<UnifeedObjects.FilterValueModel>()
						{
							new UnifeedObjects.FilterValueModel() { Code = "MC000178" },
						}
					}
				}
			};

			var postdataJson = JsonConvert.SerializeObject(searchParms);
			byte[] postdata = Encoding.UTF8.GetBytes(postdataJson);
			string headers = $"Content-Type: application/json";
			browser.Navigate(url, string.Empty, postdata, headers);
		}

		private void Browser_Navigating(object sender, WebBrowserNavigatingEventArgs e)
		{
			Log($"Navigating: {e.Url}");
			if (e.Url.Scheme == UnifeedSchemeName)
			{
				e.Cancel = true;

				Log($"Interfaced! {e.Url}");
				var queryString = HttpUtility.ParseQueryString(e.Url.Query);

				string data = string.Empty;
				if ("Filter".Equals(queryString["type"], StringComparison.OrdinalIgnoreCase))
				{
					// Retrieve object at the Unifeed API
					using (var wc = new WebClient())
					{
						var id = queryString["id"];
						data = wc.DownloadString($"{UnifeedApiUrl}/Filter/{id}");
					}
				}

				Log($"Retrieved data: {data}");

				if (!string.IsNullOrEmpty(_currentToken.RefreshToken))
				{
					// Refresh token. Normally this is not needed for every call, only when the token is expired.
					// Only possible when offline_access scope is honored
					RefreshToken();
				}

				// Restart Unifeed
				// StartUnifeed();
				browser.GoBack();
			}
		}

		private void Browser_Navigated(object sender, System.Windows.Forms.WebBrowserNavigatedEventArgs e)
		{
			Log($"Navigated: {e.Url}");
		}

		private void Log(string tekst)
		{
			logBox.Text += $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")} {tekst} {Environment.NewLine}";
			System.Diagnostics.Debug.WriteLine($"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")} {tekst} {Environment.NewLine}");
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

			_currentToken = SharedCode.Authentication.TokenService.RetrieveToken(AuthorizeTokenUrl, query);

			Log($"Refreshing tokens. New token retrieved: {_currentToken.TokenIssued.ToString("yyyyMMdd_HHmmss")}");
		}
	}
}
