namespace Bassia.Server;

using System.Net;
using Microsoft.AspNetCore.Http;

/// <summary>
/// How a browser signs in to the dashboard: a form that takes the server's token and leaves a cookie, so the token never
/// has to sit in a URL. API clients present the token as a bearer token instead.
/// </summary>
internal static class SignIn
{
	public const string Path = "/login";

	public const string Cookie = "bassia_token";

	/// <summary>A page load, as opposed to a script or a command-line tool, which should see a 401 rather than a form.</summary>
	public static bool IsBrowserPage(HttpRequest request) =>
		!request.Path.StartsWithSegments("/fragment") && request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

	public static string PathFor(string target) => $"{Path}?next={Uri.EscapeDataString(target)}";

	/// <summary>The URL asked for, without any token in it.</summary>
	public static string PathWithoutToken(HttpRequest request) =>
		request.Path + QueryString.Create(request.Query.Where(item => item.Key != "token"));

	/// <summary>Only ever a path on this server: "//host" and "/\host" are other sites to a browser.</summary>
	public static string SafeTarget(string? target) =>
		target is { Length: > 0 } && target[0] == '/' && !target.StartsWith("//", StringComparison.Ordinal) && !target.Contains('\\') ? target : "/";

	public static void SetCookie(HttpContext context, string token, string publicUrl) =>
		context.Response.Cookies.Append(Cookie, token, new CookieOptions
		{
			HttpOnly = true,
			SameSite = SameSiteMode.Strict,
			Path = "/",
			Secure = context.Request.IsHttps || publicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
		});

	public static Task WritePageAsync(HttpContext context, string target, string? error)
	{
		context.Response.ContentType = "text/html; charset=utf-8";
		context.Response.Headers.CacheControl = "no-store";
		var message = error is null ? "" : $"<p class=\"error\">{WebUtility.HtmlEncode(error)}</p>";
		return context.Response.WriteAsync($"""
			<!DOCTYPE html>
			<html lang="en">
			<head><meta charset="utf-8"><title>Sign in - Bassia</title><link rel="stylesheet" href="/style.css"></head>
			<body>
			<main style="max-width: 32rem; margin: 4rem auto; padding: 0 1rem">
			<h1>Sign in to Bassia</h1>
			{message}
			<form method="post" action="{WebUtility.HtmlEncode(PathFor(target))}">
			<p><label for="token">This server's token</label></p>
			<p><input id="token" name="token" type="password" autocomplete="current-password" autofocus required style="width: 100%"></p>
			<p><button type="submit">Sign in</button></p>
			</form>
			<p>It is the server's <code>-token</code>. A server started without one keeps its own in the file <code>token</code> of its data folder.</p>
			</main>
			</body>
			</html>
			""", context.RequestAborted);
	}
}
