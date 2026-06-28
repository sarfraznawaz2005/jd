namespace JustDownload.App.ViewModels;

/// <summary>
/// A download link handed off by the browser extension (TASK-091), carrying the auth context the extension
/// captured from the browser session so an authenticated/signed download succeeds: the page
/// <see cref="Referrer"/> and the request <see cref="Cookies"/> (a <c>Cookie</c> header value). Cookies are
/// secrets — they are kept only in the OS keychain by the engine, never persisted in plaintext.
/// </summary>
/// <param name="Url">The download URL.</param>
/// <param name="Referrer">The referring page URL, if any.</param>
/// <param name="Cookies">The captured cookies as a <c>Cookie</c> header value, if any.</param>
public sealed record BrowserLinkHandoff(string Url, string? Referrer, string? Cookies);
