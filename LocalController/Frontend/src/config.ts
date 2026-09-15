// Runtime configuration, read from <meta> tags in index.html so that no
// inline script is needed (keeps the Content-Security-Policy strict). The
// {{placeholders}} in those tags are filled in by the C# server.

function meta(name: string): string | undefined {
    return document.querySelector<HTMLMetaElement>(`meta[name="${name}"]`)?.content;
}

export const config = {
    apiBase:          meta('api-base')         ?? '/api/v1',
    frontendVersion:  meta('frontend-version') ?? '?',
    serverVersion:    meta('server-version')   ?? '?'
};
