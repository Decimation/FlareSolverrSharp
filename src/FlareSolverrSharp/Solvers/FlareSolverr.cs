﻿using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FlareSolverrSharp.Constants;
using FlareSolverrSharp.Exceptions;
using FlareSolverrSharp.Types;
using FlareSolverrSharp.Utilities;

namespace FlareSolverrSharp.Solvers;

public class FlareSolverr
{

	internal static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerOptions.Default)
	{
		DefaultIgnoreCondition =
			JsonIgnoreCondition.WhenWritingDefault,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		IncludeFields        = true,
	};

	internal const int MAX_TIMEOUT_DEFAULT = 60000;

	private static readonly SemaphoreLocker s_locker = new SemaphoreLocker();

	private HttpClient m_httpClient;

	public Uri FlareSolverrUri { get; }

	public FlareSolverrCommon FlareSolverrCommon { get; }

	public FlareSolverr(string flareSolverrApiUrl, FlareSolverrCommon common = null)
	{
		var apiUrl = flareSolverrApiUrl;

		if (!apiUrl.EndsWith("/"))
			apiUrl += "/";

		FlareSolverrUri    = new Uri($"{apiUrl}v1");
		FlareSolverrCommon = (common ?? new FlareSolverrCommon());
	}

	public Task<FlareSolverrResponse> SolveAsync(HttpRequestMessage request, string sessionId = null)
	{
		var content = GenerateFlareSolverrRequest(request, sessionId);
		return SendFlareSolverrRequestAsync(content);
	}

	public Task<FlareSolverrResponse> CreateSessionAsync()
	{
		var req = new FlareSolverrRequestGet
		{
			Command    = CloudflareValues.CMD_SESSIONS_CREATE,
			MaxTimeout = FlareSolverrCommon.MaxTimeout,
			Proxy      = GetProxy()
		};
		return SendFlareSolverrRequestAsync(GetSolverRequestContent(req));
	}

	public Task<FlareSolverrResponse> ListSessionsAsync()
	{
		var req = new FlareSolverrRequestGet
		{
			Command    = CloudflareValues.CMD_SESSIONS_LIST,
			MaxTimeout = FlareSolverrCommon.MaxTimeout,
			Proxy      = GetProxy()
		};
		return SendFlareSolverrRequestAsync(GetSolverRequestContent(req));
	}

	public Task<FlareSolverrResponse> DestroySessionAsync(string sessionId)
	{
		var req = new FlareSolverrRequestGet
		{
			Command    = CloudflareValues.CMD_SESSIONS_DESTROY,
			MaxTimeout = FlareSolverrCommon.MaxTimeout,
			Proxy      = GetProxy(),
			Session    = sessionId
		};
		return SendFlareSolverrRequestAsync(GetSolverRequestContent(req));
	}

	private async Task<FlareSolverrResponse> SendFlareSolverrRequestAsync(HttpContent flareSolverrRequest)
	{
		FlareSolverrResponse result = null;

		await s_locker.LockAsync(async () =>
		{
			HttpResponseMessage response;

			try {
				m_httpClient = new HttpClient();

				// wait 5 more seconds to make sure we return the FlareSolverr timeout message
				m_httpClient.Timeout = TimeSpan.FromMilliseconds(FlareSolverrCommon.MaxTimeout + 5000);
				response             = await m_httpClient.PostAsync(FlareSolverrUri, flareSolverrRequest);
			}
			catch (HttpRequestException e) {
				throw new FlareSolverrException($"Error connecting to FlareSolverr server: {e}");
			}
			catch (Exception e) {
				throw new FlareSolverrException($"Exception: {e}");
			}
			finally {
				m_httpClient.Dispose();
			}

			// Don't try parsing if FlareSolverr hasn't returned 200 or 500
			if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.InternalServerError) {
				throw new FlareSolverrException($"HTTP StatusCode not 200 or 500. Status is :{response.StatusCode}");
			}

			var resContent = await response.Content.ReadAsStringAsync();
			// var resContent = await response.Content.ReadAsStreamAsync();

			try {
				var options = new JsonSerializerOptions()
				{
					// PropertyNameCaseInsensitive = true,
					// PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
					IncludeFields               = true,
					// NumberHandling              = JsonNumberHandling.Strict | JsonNumberHandling.AllowReadingFromString,

					// DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
				};
				// result  = await JsonSerializer.DeserializeAsync<FlareSolverrResponse>(resContent, options);

				result = JsonSerializer.Deserialize<FlareSolverrResponse>(resContent, options);
			}
			catch (Exception) {
				throw new FlareSolverrException($"Error parsing response, check FlareSolverr. Response: {resContent}");
			}

			try {
				Enum.TryParse(result.Status, true, out FlareSolverrStatusCode returnStatusCode);

				if (returnStatusCode == FlareSolverrStatusCode.ok) {
					return result;

				}
				else {
					string errMsg = returnStatusCode switch
					{
						FlareSolverrStatusCode.warning =>
							$"FlareSolverr was able to process the request, but a captcha was detected. Message: {result.Message}",
						FlareSolverrStatusCode.error =>
							$"FlareSolverr was unable to process the request, please check FlareSolverr logs. Message: {result.Message}",
						_ =>
							$"Unable to map FlareSolverr returned status code, received code: {result.Status}. Message: {result.Message}"
					};
					throw new FlareSolverrException(errMsg);

				}

			}
			catch (ArgumentException) {
				throw new FlareSolverrException(
					$"Error parsing status code, check FlareSolverr log. Status: {result.Status}. Message: {result.Message}");
			}
		});

		return result;
	}

	private FlareSolverrRequestProxy GetProxy()
	{
		FlareSolverrRequestProxy proxy = null;

		if (!string.IsNullOrWhiteSpace(FlareSolverrCommon.ProxyUrl)) {
			proxy = new FlareSolverrRequestProxy
			{
				Url = FlareSolverrCommon.ProxyUrl,
			};

			if (!string.IsNullOrWhiteSpace(FlareSolverrCommon.ProxyUsername)) {
				proxy.Username = FlareSolverrCommon.ProxyUsername;
			}

			if (!string.IsNullOrWhiteSpace(FlareSolverrCommon.ProxyPassword)) {
				proxy.Password = FlareSolverrCommon.ProxyPassword;
			}

		}

		return proxy;
	}

	private HttpContent GetSolverRequestContent(FlareSolverrRequest request)
	{
		var payload = JsonContent.Create(request, options: JsonSerializerOptions);

		// HttpContent content = new StringContent(payload, Encoding.UTF8, "application/json");
		// return content;
		return payload;
	}

	private HttpContent GenerateFlareSolverrRequest(HttpRequestMessage request, string sessionId = null)
	{
		FlareSolverrRequest req;

		if (string.IsNullOrWhiteSpace(sessionId))
			sessionId = null;

		var url = request.RequestUri.ToString();

		FlareSolverrRequestProxy proxy = GetProxy();

		if (request.Method == HttpMethod.Get) {
			req = new FlareSolverrRequestGet
			{
				Command    = CloudflareValues.CMD_REQUEST_GET,
				Url        = url,
				MaxTimeout = FlareSolverrCommon.MaxTimeout,
				Proxy      = proxy,
				Session    = sessionId
			};
		}
		else if (request.Method == HttpMethod.Post) {
			// request.Content.GetType() doesn't work well when encoding != utf-8
			var contentMediaType = request.Content.Headers.ContentType?.MediaType.ToLower() ?? "<null>";

			if (contentMediaType.Contains("application/x-www-form-urlencoded")) {
				req = new FlareSolverrRequestPost
				{
					Command    = CloudflareValues.CMD_REQUEST_POST,
					Url        = url,
					PostData   = request.Content.ReadAsStringAsync().Result,
					MaxTimeout = FlareSolverrCommon.MaxTimeout,
					Proxy      = proxy,
					Session    = sessionId
				};
			}
			else if (contentMediaType.Contains("multipart/form-data")
			         || contentMediaType.Contains("text/html")) {
				//TODO Implement - check if we just need to pass the content-type with the relevant headers
				throw new FlareSolverrException($"Unimplemented POST Content-Type: {contentMediaType}");
			}
			else {
				throw new FlareSolverrException($"Unsupported POST Content-Type: {contentMediaType}");
			}
		}
		else {
			throw new FlareSolverrException($"Unsupported HttpMethod: {request.Method}");
		}

		return GetSolverRequestContent(req);
	}

}