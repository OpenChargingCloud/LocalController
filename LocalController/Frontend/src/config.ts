// Runtime configuration, read from <meta> tags in index.html so that no
// inline script is needed (keeps the Content-Security-Policy strict). The
// {{placeholders}} in those tags are filled in by the C# server.

function meta(name: string): string | undefined {
    return document.querySelector<HTMLMetaElement>(`meta[name="${name}"]`)?.content;
}

export const config = {
    apiBase:          meta('api-base')         ?? '/api/v1',

    // Signing in happens at Hermod's HTTPExt API, not at this controller's own
    // API: it is the only place that can check a password. Everything after
    // the sign-in goes to apiBase with the cookie it set.
    extBase:          meta('ext-base')         ?? '/ext',
    frontendVersion:  meta('frontend-version') ?? '?',
    serverVersion:    meta('server-version')   ?? '?'
};
