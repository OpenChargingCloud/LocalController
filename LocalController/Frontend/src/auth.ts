import { api, ApiError, onUnauthorized, type Me, type Permission } from './api/client';
import { fromURL } from './basePath';

type Listener = (user: Me | null) => void;

/**
 * Who is signed in to the web interface. The truth lives in the session cookie
 * and on the local controller; this is a cache of /auth/me so that the router's guard
 * and the pages can react without asking every time.
 */
class AuthState {

    user: Me | null = null;

    private readonly listeners = new Set<Listener>();

    /** Ask the local controller who we are; null when nobody is signed in. */
    async refresh(): Promise<Me | null> {

        try
        {
            this.set(await api.auth.me());
        }
        catch (error)
        {

            if (!(error instanceof ApiError && error.isUnauthorized))
                console.warn('Could not load the session:', error);

            this.set(null);

        }

        return this.user;

    }

    set(user: Me | null): void {
        this.user = user;
        for (const listener of this.listeners)
            listener(user);
    }

    onChange(listener: Listener): () => void {
        this.listeners.add(listener);
        return () => this.listeners.delete(listener);
    }

    async signOut(): Promise<void> {
        try {
            await api.auth.logout();
        }
        finally {
            this.set(null);
        }
    }

    /**
     * Whether the person signed in may do this.
     *
     * Used to grey a control out, never to decide anything: the local controller checks
     * every request again when it arrives, so a page that got this wrong shows
     * a button that answers 403 rather than one that works.
     */
    can(permission: Permission): boolean {
        return this.user?.permissions?.includes(permission) ?? false;
    }

    /** Router guard: the sign-in page with a way back, or null when signed in. */
    readonly requireSignIn = (url: URL): string | null =>
        this.user
            ? null
            // The route and not the address bar's path: what comes back here
            // goes through the router, which puts the base back on. Carrying
            // "/EV/logs" through a sign-in would come back as "/EV/EV/logs".
            : `/login?next=${encodeURIComponent(fromURL(url.pathname) + url.search)}`;

}

export const auth = new AuthState();

// A 401 from any request means the session is gone (expired, signed out
// elsewhere, the local controller restarted): forget the user, the guards do the rest.
onUnauthorized(() => {
    if (auth.user !== null)
        auth.set(null);
});
