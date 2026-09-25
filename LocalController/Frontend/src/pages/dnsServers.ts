import type { DNSConfiguration } from '../api/client';

/**
 * How long the DNS page waits for the local controller to look something up.
 *
 * Apart from the page, because this is the part that decides when the page
 * stops believing in the local controller - and it was wrong in a way nobody sees until
 * a name server does not answer: it added the servers' timeouts up, as if the
 * local controller asked them one after another, when it asks all of them at once.
 */


/**
 * The longest one name server can honestly take, in seconds.
 *
 * Its own timeout where it has one and the client's where it has not, times
 * the number of attempts - and the attempts are the part that is easy to
 * forget: measured against a name server that does not answer at all, a query
 * with a ten second timeout came back after twenty, because the local controller tries
 * again. A deadline of ten would have given up on a local controller that was still
 * doing what it was told.
 *
 * @param configuration  what the local controller said about its name resolution.
 * @param index          the server's place in the list.
 */
export function oneServerTakes(configuration: DNSConfiguration | null, index: number): number {

    const timeout = configuration?.servers[index]?.queryTimeoutSeconds ??
                    configuration?.settings.queryTimeoutSeconds ?? 0;

    return timeout * ((configuration?.settings.maxRetries ?? 0) + 1);

}


/**
 * The longest all of them can honestly take, in seconds: the longest any one
 * of them can.
 *
 * Not their sum. The local controller asks every server at once and takes the first
 * usable answer, so a lookup that nobody answers ends when the slowest of them
 * gives up - measured on a WWCP node, two name servers that never answer, at
 * three seconds each, took 3.0 seconds, and not 6.
 *
 * @param configuration  what the local controller said about its name resolution.
 */
export function allServersTake(configuration: DNSConfiguration | null): number {
    return Math.max(0, ...(configuration?.servers ?? []).map((_, index) => oneServerTakes(configuration, index)));
}
