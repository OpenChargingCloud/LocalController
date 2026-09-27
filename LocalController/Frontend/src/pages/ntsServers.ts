import type { NTSServerEntry, NTSTimeSource } from '../api/client';
import { draftOf, withPins } from './pins';

/**
 * The list of time servers as the NTS page edits it.
 *
 * Apart from the page, because this is the part that decides what the local controller
 * is told - and the local controller is told the whole list every time, so a mistake
 * here is a server deleted that nobody touched. The page around it only draws
 * and asks.
 */


/** The ports a server is asked on unless its entry says otherwise. */
export interface UsualPorts {
    ntsKE:  number;
    ntp:    number;
}


/**
 * A name as somebody reads it: without the root's dot. The local controller hands its
 * names back fully qualified, and "ptbtime1.ptb.de." is correct and looks like
 * a typing mistake.
 */
export function readable(hostname: string): string {
    return hostname.endsWith('.') ? hostname.slice(0, -1) : hostname;
}


/**
 * Whether two names are the same server's: compared without the root's dot,
 * and without case.
 */
function sameName(one: string, other: string): boolean {
    return readable(one.trim()).toLowerCase() === readable(other.trim()).toLowerCase();
}


/**
 * A server as the local controller shows it, turned back into what its configuration
 * says - with everything that is the usual left out, and what it is held to
 * kept in.
 *
 * Left out rather than repeated, because the local controller writes back what it is
 * sent: an entry carrying the usual ports and priority 0 becomes an object in
 * the file where a bare name was, and the file stops reading the way somebody
 * would have written it.
 *
 * What it is held to in every entry, in the keys the file writes it under,
 * because the list sent replaces the local controller's whole: an entry
 * without it is a server held to nothing from then on. Measured so: the save
 * of ptbtime2.ptb.de's priority took the root ptbtime1.ptb.de had learned on
 * first use, and the instruction to learn it, out of the file and out of
 * effect, and the log said no more than that it was held to no fingerprint.
 */
export function entryOf(source: NTSTimeSource, usual: UsualPorts): NTSServerEntry {

    const entry: NTSServerEntry = { hostname: readable(source.hostname) };

    if (source.priority  !== 0)            entry.priority   = source.priority;
    if (source.ntsKEPort !== usual.ntsKE)  entry.ntsKEPort  = source.ntsKEPort;
    if (source.ntpPort   !== usual.ntp)    entry.ntpPort    = source.ntpPort;
    if (!source.enabled)                   entry.enabled    = false;

    return withPins(entry, draftOf(source.heldTo));

}


/**
 * What the dialog saves: what was typed into it, and what the server it edits
 * is held to - as long as it is still that server.
 *
 * The dialog asks for the name, the priority, the ports and whether to ask the
 * server, and not for what it is held to; a server saved from it must not come
 * out of it held to nothing. A server given another name is another server,
 * though: a certificate pin is the fingerprint of one server's certificate, a
 * learned root is what one server was first believed with, and the local
 * controller keeps both by name. They stay behind with the old name, and the
 * new one is held to what every server is held to until somebody says
 * otherwise.
 *
 * @param shown  the server as the local controller showed it, or null for a new one.
 * @param typed  the entry made of what the dialog's fields say.
 */
export function savedFromDialog(shown:  NTSTimeSource | null,
                                typed:  NTSServerEntry): NTSServerEntry {

    return shown !== null && sameName(shown.hostname, typed.hostname)
               ? withPins(typed, draftOf(shown.heldTo))
               : typed;

}


/**
 * The list with one server replaced, or with one added at the end when there
 * is no place given.
 *
 * A new list rather than the old one changed: the old one is what the local controller
 * still has, and it is what the page has to go back to when the local controller says
 * no.
 */
export function withServer(list:   readonly NTSServerEntry[],
                           index:  number | null,
                           entry:  NTSServerEntry): NTSServerEntry[] {

    return index === null
               ? [...list, entry]
               : list.map((other, at) => at === index ? entry : other);

}


/** The list without the server at that place. */
export function withoutServer(list:   readonly NTSServerEntry[],
                              index:  number): NTSServerEntry[] {

    return list.filter((_, at) => at !== index);

}


/**
 * Whether another server of the list already has this name - the one at the
 * place being edited does not count, or a server could not be saved unchanged.
 *
 * Compared the way names compare: without the root's dot, and without case.
 */
export function nameTaken(list:      readonly NTSServerEntry[],
                          hostname:  string,
                          except:    number | null): boolean {

    return list.some((other, at) => at !== except &&
                                    sameName(other.hostname, hostname));

}
