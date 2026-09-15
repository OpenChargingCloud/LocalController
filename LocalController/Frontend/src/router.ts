// A small History-API router.
//
// The server answers every URL without a file extension with index.html, so
// deep links and reloads work; this router then renders the page matching
// location.pathname. Clicks on same-origin links that match a route are
// intercepted (pushState); everything else (files, /api/..., unknown paths)
// stays a normal browser navigation.
//
// A page may return a cleanup function from render(); it is called before the
// next page renders, e.g. to close an EventSource or destroy a chart.

export type Params   = Record<string, string>;
export type Cleanup  = () => void;

export interface PageContext {
    /** The element to render into; already emptied. */
    root:      HTMLElement;
    params:    Params;
    url:       URL;
    navigate:  (path: string, replace?: boolean) => void;
}

export interface Page {
    title:   string | ((params: Params) => string);
    render:  (context: PageContext) => void | Cleanup | Promise<void | Cleanup>;
}

/**
 * Decides whether a route may be rendered: null to go ahead, or a path to
 * navigate to instead (e.g. the sign-in page for a protected route).
 */
export type Guard = (url: URL) => string | null;

export interface Route {
    /** "/", "/devices", "/devices/:id" */
    path:    string;
    page:    Page;
    guard?:  Guard;
}

interface CompiledRoute {
    pattern:  RegExp;
    keys:     string[];
    page:     Page;
    guard?:   Guard;
}

export interface RouterOptions {
    routes:        Route[];
    outlet:        HTMLElement;
    notFound:      Page;
    titleSuffix?:  string;
    /** Called after every navigation, e.g. to highlight the active menu entry. */
    onNavigated?:  (url: URL) => void;
}


export class Router {

    private readonly routes:       CompiledRoute[];
    private readonly outlet:       HTMLElement;
    private readonly notFound:     Page;
    private readonly titleSuffix:  string;
    private readonly onNavigated?: (url: URL) => void;

    private renderToken  = 0;
    private cleanup?:    Cleanup;

    constructor(options: RouterOptions) {
        this.routes       = options.routes.map(compile);
        this.outlet       = options.outlet;
        this.notFound     = options.notFound;
        this.titleSuffix  = options.titleSuffix ?? '';
        this.onNavigated  = options.onNavigated;
    }

    start(): void {
        window.addEventListener('popstate', () => void this.render());
        document.addEventListener('click', event => this.onClick(event));
        void this.render();
    }

    navigate(path: string, replace = false): void {

        if (replace)
            history.replaceState(null, '', path);
        else
            history.pushState(null, '', path);

        void this.render();

    }

    matches(pathname: string): boolean {
        return this.match(pathname) !== null;
    }


    private onClick(event: MouseEvent): void {

        if (event.defaultPrevented || event.button !== 0 ||
            event.metaKey || event.ctrlKey || event.shiftKey || event.altKey)
            return;

        const anchor = (event.target as Element | null)?.closest('a');

        if (!anchor || (anchor.target && anchor.target !== '_self') ||
            anchor.hasAttribute('download') || anchor.origin !== location.origin)
            return;

        // Not one of our pages: let the browser fetch it (files, the JSON API,
        // unknown paths that the server answers with the stub anyway).
        if (!this.matches(anchor.pathname))
            return;

        event.preventDefault();

        const target = anchor.pathname + anchor.search + anchor.hash;

        if (target !== location.pathname + location.search + location.hash)
            this.navigate(target);

    }

    private match(pathname: string): { page: Page; params: Params; guard?: Guard } | null {

        const path = pathname.length > 1 && pathname.endsWith('/')
                         ? pathname.slice(0, -1)
                         : pathname;

        for (const route of this.routes)
        {

            const found = route.pattern.exec(path);

            if (found === null)
                continue;

            const params: Params = {};

            route.keys.forEach((key, index) => {
                params[key] = decodeURIComponent(found[index + 1] ?? '');
            });

            return { page: route.page, params, guard: route.guard };

        }

        return null;

    }

    private async render(): Promise<void> {

        const token    = ++this.renderToken;
        const url      = new URL(location.href);
        const matched  = this.match(url.pathname);
        const page     = matched?.page   ?? this.notFound;
        const params   = matched?.params ?? {};

        // A protected route sends the visitor elsewhere, e.g. to sign in.
        const redirect = matched?.guard?.(url);

        if (redirect) {
            this.navigate(redirect, true);
            return;
        }

        this.runCleanup();

        const title    = typeof page.title === 'function' ? page.title(params) : page.title;
        document.title = title + this.titleSuffix;

        this.outlet.replaceChildren();
        this.onNavigated?.(url);
        window.scrollTo(0, 0);

        try
        {

            const result = await page.render({
                root:      this.outlet,
                params,
                url,
                navigate:  (path, replace) => this.navigate(path, replace)
            });

            if (typeof result === 'function') {

                // A navigation happened while this page was still loading.
                if (token !== this.renderToken)
                    result();
                else
                    this.cleanup = result;

            }

        }
        catch (error)
        {

            if (token !== this.renderToken)
                return;

            const message = error instanceof Error ? error.message : String(error);

            this.outlet.innerHTML = '';

            const box = document.createElement('div');
            box.className    = 'error-box';
            box.textContent  = `This page could not be rendered: ${message}`;

            this.outlet.appendChild(box);

        }

    }

    private runCleanup(): void {

        const cleanup = this.cleanup;
        this.cleanup  = undefined;

        try
        {
            cleanup?.();
        }
        catch (error)
        {
            console.error('Page cleanup failed:', error);
        }

    }

}


function compile(route: Route): CompiledRoute {

    const keys: string[] = [];

    const pattern = route.path.
                        split('/').
                        map(segment => {

                            if (segment.startsWith(':')) {
                                keys.push(segment.slice(1));
                                return '([^/]+)';
                            }

                            return segment.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

                        }).
                        join('/');

    return {
        pattern:  new RegExp(`^${pattern === '' ? '/' : pattern}$`),
        keys,
        page:     route.page,
        guard:    route.guard
    };

}
