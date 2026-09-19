// Where this web interface is mounted.
//
// On its own each of these programs is at the root of its own port, and the
// base path is the empty string - which is what every one of these functions
// does nothing for. It is something else only when several of them share one
// HTTP server and are told apart by the first path segment: /EV/...,
// /ChargingStation/..., /LocalController/..., /CSMS/...
//
// The server fills the <meta name="base"> tag in the stub, so the bundle is
// the same either way and never has to be rebuilt for a different mount point.
//
// Routes are written WITHOUT the base everywhere else in this bundle - in the
// router's table, in the menu, in the sign-in's "next" - and it is added on
// the way into the address bar and taken off on the way out. One place that
// knows, rather than a prefix threaded through every page.

import { config } from './config';


/** "" at the root, "/EV" and the like below one. Never a trailing slash. */
export const basePath: string = (config.base ?? '').replace(/\/+$/, '');


/**
 * A route as the address bar should show it.
 *
 * Idempotent: a path that already carries the base comes back unchanged, so a
 * link's own absolute pathname can be handed to it without being taken apart
 * first.
 */
export function toURL(path: string): string {

    if (basePath === '' || isBelowBase(path))
        return path;

    return basePath + (path.startsWith('/') ? path : '/' + path);

}


/** The route inside a path from the address bar, without the base. */
export function fromURL(pathname: string): string {

    if (basePath === '' || !isBelowBase(pathname))
        return pathname;

    // "/EV" alone is the root of this application, not the empty path.
    return pathname.length === basePath.length
               ? '/'
               : pathname.slice(basePath.length);

}


/**
 * Whether a path is below the base - on a segment boundary and not merely by
 * its first characters.
 *
 * The distinction is not theoretical: the charging station's base is
 * "/ChargingStation" and one of its own routes is "/charging", and a station
 * mounted at "/EV" would otherwise swallow its own "/EVSEs".
 */
function isBelowBase(path: string): boolean {
    return path === basePath || path.startsWith(basePath + '/');
}
