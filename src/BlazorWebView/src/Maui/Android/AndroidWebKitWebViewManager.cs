﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Android.Webkit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using AUri = Android.Net.Uri;
using AWebView = Android.Webkit.WebView;

namespace Microsoft.AspNetCore.Components.WebView.Maui
{
	/// <summary>
	/// An implementation of <see cref="WebViewManager"/> that uses the Android WebKit WebView browser control
	/// to render web content.
	/// </summary>
	[SupportedOSPlatform("android23.0")]
	internal class AndroidWebKitWebViewManager : WebViewManager
	{
		// Using an IP address means that WebView doesn't wait for any DNS resolution,
		// making it substantially faster. Note that this isn't real HTTP traffic, since
		// we intercept all the requests within this origin.
		private static readonly string AppOrigin = $"https://{BlazorWebView.AppHostAddress}/";
		private static readonly Uri AppOriginUri = new(AppOrigin);
		private static readonly AUri AndroidAppOriginUri = AUri.Parse(AppOrigin)!;
		private readonly AWebView _webview;
		private readonly string _contentRootRelativeToAppRoot;
		private WebMessagePort[]? _nativeToJSPorts;

		/// <summary>
		/// Constructs an instance of <see cref="AndroidWebKitWebViewManager"/>.
		/// </summary>
		/// <param name="webview">A wrapper to access platform-specific WebView APIs.</param>
		/// <param name="services">A service provider containing services to be used by this class and also by application code.</param>
		/// <param name="dispatcher">A <see cref="Dispatcher"/> instance that can marshal calls to the required thread or sync context.</param>
		/// <param name="fileProvider">Provides static content to the webview.</param>
		/// <param name="contentRootRelativeToAppRoot">Path to the directory containing application content files.</param>
		/// <param name="hostPageRelativePath">Path to the host page within the <paramref name="fileProvider"/>.</param>
		public AndroidWebKitWebViewManager(AWebView webview!!, IServiceProvider services, Dispatcher dispatcher, IFileProvider fileProvider, JSComponentConfigurationStore jsComponents, string contentRootRelativeToAppRoot, string hostPageRelativePath)
			: base(services, dispatcher, AppOriginUri, fileProvider, jsComponents, hostPageRelativePath)
		{
#if WEBVIEW2_MAUI
			if (services.GetService<MauiBlazorMarkerService>() is null)
			{
				throw new InvalidOperationException(
					"Unable to find the required services. " +
					$"Please add all the required services by calling '{nameof(IServiceCollection)}.{nameof(BlazorWebViewServiceCollectionExtensions.AddMauiBlazorWebView)}' in the application startup code.");
			}
#endif
			_webview = webview;
			_contentRootRelativeToAppRoot = contentRootRelativeToAppRoot;
		}

		/// <inheritdoc />
		protected override void NavigateCore(Uri absoluteUri)
		{
			_webview.LoadUrl(absoluteUri.AbsoluteUri);
		}

		/// <inheritdoc />
		protected override void SendMessage(string message)
		{
			_webview.PostWebMessage(new WebMessage(message), AndroidAppOriginUri);
		}

		internal bool TryGetResponseContentInternal(string uri, bool allowFallbackOnHostPage, out int statusCode, out string statusMessage, out Stream content, out IDictionary<string, string> headers)
		{
			var defaultResult = TryGetResponseContent(uri, allowFallbackOnHostPage, out statusCode, out statusMessage, out content, out headers);
			var hotReloadedResult = StaticContentHotReloadManager.TryReplaceResponseContent(_contentRootRelativeToAppRoot, uri, ref statusCode, ref content, headers);
			return defaultResult || hotReloadedResult;
		}

		internal void SetUpMessageChannel()
		{
			_nativeToJSPorts = _webview.CreateWebMessageChannel();

			var nativeToJs = new BlazorWebMessageCallback(message =>
			{
				MessageReceived(AppOriginUri, message!);
			});

			var destPort = new[] { _nativeToJSPorts[1] };

			_nativeToJSPorts[0].SetWebMessageCallback(nativeToJs);

			_webview.PostWebMessage(new WebMessage("capturePort", destPort), AndroidAppOriginUri);
		}

		protected override async ValueTask DisposeAsyncCore()
		{
			await base.DisposeAsyncCore();

			if (_nativeToJSPorts is not null)
			{
				foreach (var port in _nativeToJSPorts)
				{
					port?.Close();
				}
			}
		}

		private class BlazorWebMessageCallback : WebMessagePort.WebMessageCallback
		{
			private readonly Action<string?> _onMessageReceived;

			public BlazorWebMessageCallback(Action<string?> onMessageReceived)
			{
				_onMessageReceived = onMessageReceived ?? throw new ArgumentNullException(nameof(onMessageReceived));
			}

			public override void OnMessage(WebMessagePort? port, WebMessage? message)
			{
				if (message is null)
				{
					throw new ArgumentNullException(nameof(message));
				}

				_onMessageReceived(message.Data);
			}
		}
	}
}
