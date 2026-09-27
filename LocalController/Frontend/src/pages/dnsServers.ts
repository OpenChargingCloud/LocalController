import type { DNSConfiguration, DNSServerEntry } from '../api/client';
import { pinsIn, withPins } from './pins';

/**
 * How long the DNS page waits for the local controller to look something up,
 * and what it tells the local controller about a name server.
 *
 * Apart from the page, because these are the parts that decide when the page
 * stops believing in the local controller and what the local controller is
 * told - and the first was wrong in a way nobody sees until a name server does
 * not answer: it added the servers' timeouts up, as if the local controller
 * asked them one after another, when it asks all of them at once.
 */


/**
 * Whether a name server asked over this transport shows a certificate: over
 * TLS and over HTTPS, in all its forms, and over nothing else.
 */
export function isEncrypted(transport: string): boolean {

    const name = transport.toUpperCase();

    return name === 'TLS' || name.startsWith('HTTPS');

}


/**
 * A name server as the local controller is told it: what its configuration
 * keeps, in the order the file keeps it, and none of what the controller only
 * says about it - what was made of its certificate, what it was last believed
 * with.
 *
 * What it is held to only where it is asked over TLS or HTTPS. The controller
 * refuses a pin on a server that shows no certificate, rightly: somebody would
 * believe it held to something it is never compared with. So a server switched
 * to UDP lets go of its pins when it is saved, and keeps them on the page until
 * then, for whoever switches it back. Measured the other way round: sent as
 * the page had it, 1.1.1.1 switched from TLS to UDP was refused with the
 * whole save, and nothing on the page could take its pins off.
 */
export function entryOf(server: DNSServerEntry): DNSServerEntry {

    const entry: DNSServerEntry = {
        address:              server.address,
        port:                 server.port,
        transport:            server.transport,
        queryTimeoutSeconds:  server.queryTimeoutSeconds
    };

    return isEncrypted(server.transport)
               ? withPins(entry, pinsIn(server))
               : entry;

}


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
