import type { Certificate, CertificateStore, JudgementOutcome, PinKeys, PinMismatch, ServerJudgement, ServerPins, TrustOnFirstUse } from '../api/client';

/**
 * What a time server or a name server is held to, as the pages edit it.
 *
 * Apart from the pages, because this is the part that decides what the local
 * controller is told - and it is told a whole list of servers at a time, so a
 * pin this gets wrong is a pin gone from a server nobody touched. The pages
 * around it only draw and ask.
 *
 * The vehicle's and the charging station's pages hold their servers to pins
 * the same way, from the same rules.
 */


/** What a server is held to, as a dialog edits it. */
export interface PinsDraft {
    /** The certificates it may show, any one of them. */
    certificates:     string[];
    /** The roots its chain may end at, any one of them. */
    roots:            string[];
    onMismatch:       PinMismatch;
    trustOnFirstUse:  TrustOnFirstUse | null;
}


/** The most fingerprints of one kind one server may be held to: the controller's own limit. */
export const maxPins = 16;


/** Every key an entry says what its server is held to with. */
const pinKeys = [ 'certificateFingerprint', 'certificateFingerprints',
                  'rootFingerprint',        'rootFingerprints',
                  'onMismatch',             'trustOnFirstUse' ] as const;


/** Held to nothing beyond what every server is held to. */
export function noPins(): PinsDraft {
    return { certificates: [], roots: [], onMismatch: 'refuse', trustOnFirstUse: null };
}


/**
 * A SHA-256 fingerprint in the form the controller keeps it - 64 hexadecimal
 * digits in lower case - or null where it is not one.
 *
 * Taken the ways it is written down: with colons, spaces or dashes between
 * the bytes or nothing at all, and after the "SHA256 Fingerprint=" that
 * openssl puts in front of it. Never shorter than the whole of it: a pin on
 * part of a fingerprint is a pin on every certificate sharing that part.
 */
export function normalisedFingerprint(text: string): string | null {

    const written = text.includes('=') ? text.slice(text.lastIndexOf('=') + 1) : text;
    const digits  = written.trim().replace(/[:\s-]/g, '');

    return /^[0-9a-fA-F]{64}$/.test(digits) ? digits.toLowerCase() : null;

}


/**
 * The fingerprints somebody typed or pasted, one to a line, each once - and
 * what was not one, as it was written, so that it can be said which line is
 * wrong rather than that something is.
 */
export function parseFingerprints(text: string): { fingerprints: string[]; wrong: string[] } {

    const fingerprints: string[] = [];
    const wrong:        string[] = [];

    for (const line of text.split(/[\r\n,;]+/)) {

        const written = line.trim();

        if (written.length === 0)
            continue;

        const fingerprint = normalisedFingerprint(written);

        if (fingerprint === null)
            wrong.push(written);

        else if (!fingerprints.includes(fingerprint))
            fingerprints.push(fingerprint);

    }

    return { fingerprints, wrong };

}


/** A list of fingerprints, each once and in the controller's form where it is one. */
function unique(fingerprints: readonly (string | null | undefined)[]): string[] {

    const result: string[] = [];

    for (const written of fingerprints) {

        if (written === null || written === undefined || written.trim().length === 0)
            continue;

        const fingerprint = normalisedFingerprint(written) ?? written.trim();

        if (!result.includes(fingerprint))
            result.push(fingerprint);

    }

    return result;

}


/** What the controller says a server is held to, as a draft - nothing, where it says null. */
export function draftOf(heldTo: ServerPins | null | undefined): PinsDraft {

    if (heldTo === null || heldTo === undefined)
        return noPins();

    return {
        certificates:     unique(heldTo.certificates ?? [ heldTo.certificate ]),
        roots:            unique(heldTo.roots        ?? [ heldTo.root ]),
        onMismatch:       heldTo.onMismatch      ?? 'refuse',
        trustOnFirstUse:  heldTo.trustOnFirstUse ?? null
    };

}


/**
 * What an entry says its server is held to, in whichever keys it says it -
 * one of a kind and several of a kind add up, as they do in the file.
 */
export function pinsIn(entry: PinKeys): PinsDraft {

    return {
        certificates:     unique([ entry.certificateFingerprint, ...(entry.certificateFingerprints ?? []) ]),
        roots:            unique([ entry.rootFingerprint,        ...(entry.rootFingerprints        ?? []) ]),
        onMismatch:       entry.onMismatch      ?? 'refuse',
        trustOnFirstUse:  entry.trustOnFirstUse ?? null
    };

}


/** Whether a draft holds a server to a fingerprint. */
export function isPinned(draft: PinsDraft): boolean {
    return draft.certificates.length > 0 || draft.roots.length > 0;
}


/** Whether a draft says anything at all: a fingerprint, or one to learn. */
export function saysAnything(draft: PinsDraft): boolean {
    return isPinned(draft) || draft.trustOnFirstUse !== null;
}


/**
 * A draft in the keys an entry says it with - and only what it says: a server
 * held to nothing has none of them, which is how the file says it.
 *
 * One fingerprint of a kind under the singular key and several under the
 * plural, as the controller writes them itself. What a mismatch comes to only
 * where it is not what a pin means anyway, and only where there is something
 * to mismatch: the controller refuses it on a server held to nothing, because
 * somebody who wrote it believes the server to be held to something.
 */
export function keysOf(draft: PinsDraft): PinKeys {

    const keys:          PinKeys = {};
    const certificates   = unique(draft.certificates);
    const roots          = unique(draft.roots);

    if      (certificates.length === 1)  keys.certificateFingerprint   = certificates[0];
    else if (certificates.length >  1)   keys.certificateFingerprints  = certificates;

    if      (roots.length === 1)         keys.rootFingerprint          = roots[0];
    else if (roots.length >  1)          keys.rootFingerprints         = roots;

    const something = certificates.length > 0 || roots.length > 0 || draft.trustOnFirstUse !== null;

    if (draft.onMismatch !== 'refuse' && something)
        keys.onMismatch = draft.onMismatch;

    if (draft.trustOnFirstUse !== null)
        keys.trustOnFirstUse = draft.trustOnFirstUse;

    return keys;

}


/**
 * The entry with what its server is held to replaced by the draft - every key
 * of the old pins taken out first, so that a pin written once in the singular
 * does not survive beside the same pins written again in the plural.
 *
 * A new entry rather than the old one changed: the old one is what the
 * controller still has.
 */
export function withPins<T extends PinKeys>(entry: T, draft: PinsDraft): T {

    const stripped = Object.fromEntries(Object.entries(entry).
                                               filter(([ key ]) => !(pinKeys as readonly string[]).includes(key)));

    return { ...stripped, ...keysOf(draft) } as T;

}


/**
 * What a dialog's fields say a server is held to - or the one sentence that
 * says what is wrong with them, naming the line that is.
 *
 * @param fields  the two lists of fingerprints as they were typed, and the
 *                two choices as the dialog's lists hand them over.
 */
export function readPins(fields: { certificates: string; roots: string; onMismatch: string; trustOnFirstUse: string }):
    { draft: PinsDraft; error?: undefined } | { draft?: undefined; error: string } {

    const certificates  = parseFingerprints(fields.certificates);
    const roots         = parseFingerprints(fields.roots);
    const wrong         = [ ...certificates.wrong, ...roots.wrong ];

    if (wrong.length > 0)
        return { error: `'${wrong[0]}' is not a SHA-256 fingerprint: 64 hexadecimal digits, with or without colons between them.` };

    if (certificates.fingerprints.length > maxPins || roots.fingerprints.length > maxPins)
        return { error: `A server is held to at most ${maxPins} certificates and ${maxPins} roots.` };

    const onMismatch       = fields.onMismatch === 'record' || fields.onMismatch === 'accept' ? fields.onMismatch : 'refuse';
    const trustOnFirstUse  = fields.trustOnFirstUse === 'root' || fields.trustOnFirstUse === 'certificate' ? fields.trustOnFirstUse : null;

    return {
        draft: {
            certificates:  certificates.fingerprints,
            roots:         roots.fingerprints,
            onMismatch,
            trustOnFirstUse
        }
    };

}


/** What the certificate store has to offer a server's dialog. */
export interface StoreOffers {
    /** The TLS roots a chain of this service may end at. */
    roots:         Certificate[];
    /** The server certificates kept for this service. */
    certificates:  Certificate[];
    /** What the store calls a fingerprint, whatever it keeps it as. */
    nameOf:        (fingerprint: string) => string | undefined;
}

/**
 * The roots and server certificates the store keeps for a service, to be
 * picked rather than typed: only the ones that are switched on and inside
 * their validity, and only the ones for this service or for every use - a
 * root uploaded for the name servers alone is not offered to a time server,
 * because it was uploaded to vouch for no time.
 *
 * @param service  "dns" or "nts".
 */
export function offersOf(store: CertificateStore, service: string): StoreOffers {

    const forService  = (entry: Certificate) => entry.usable &&
                                                (entry.usages === null || entry.usages === undefined || entry.usages.includes(service));

    const names       = new Map(Object.values(store.certificates ?? {}).flat().
                                       map(entry => [ entry.thumbprint.toLowerCase(), entry.label ] as const));

    return {
        roots:         (store.certificates?.tlsRoot   ?? []).filter(forService),
        certificates:  (store.certificates?.tlsServer ?? []).filter(forService),
        nameOf:        fingerprint => names.get(fingerprint.toLowerCase())
    };

}


/**
 * A fingerprint short enough for a row: its first sixteen digits, which is
 * also the handle the certificate store calls a certificate by.
 */
export function shortFingerprint(fingerprint: string): string {
    return fingerprint.length > 16 ? `${fingerprint.slice(0, 16)}…` : fingerprint;
}


/**
 * What a draft holds a server to, in words - "root ISRG Root X1 or
 * 1a2b3c4d5e6f7a8b…, refused otherwise" - or null where it holds it to
 * nothing and learns nothing.
 *
 * @param nameOf  what the certificate store calls a fingerprint, where it has
 *                it; the fingerprint's first digits otherwise.
 */
export function pinsText(draft:   PinsDraft,
                         nameOf:  (fingerprint: string) => string | undefined = () => undefined): string | null {

    const named   = (fingerprints: string[]) => fingerprints.map(fingerprint => nameOf(fingerprint) ?? shortFingerprint(fingerprint)).join(' or ');
    const parts:  string[] = [];

    if (draft.certificates.length > 0)
        parts.push(`certificate ${named(draft.certificates)}`);

    if (draft.roots.length > 0)
        parts.push(`root ${named(draft.roots)}`);

    // What it will learn only while it has not learned it yet: once it has,
    // what it learned is one of the pins above.
    const learning = draft.trustOnFirstUse === 'root'        && draft.roots.length        === 0 ||
                     draft.trustOnFirstUse === 'certificate' && draft.certificates.length === 0;

    if (learning)
        parts.push(`its ${draft.trustOnFirstUse} once it is first believed`);

    if (parts.length === 0)
        return null;

    const otherwise = draft.onMismatch === 'record' ? ', recorded otherwise'
                    : draft.onMismatch === 'accept' ? ', accepted otherwise'
                    :                                 ', refused otherwise';

    return parts.join(' and ') + otherwise;

}


/** What the controller made of a server's certificate, in a few words. */
export function outcomeText(outcome: JudgementOutcome): string {

    switch (outcome) {
        case 'accepted':       return 'believed';
        case 'recorded':       return 'used, although not what it is held to - recorded';
        case 'tolerated':      return 'used, although not what it is held to';
        case 'pinMismatch':    return 'refused: not what it is held to';
        case 'untrusted':      return 'refused: chains to no root that is trusted';
        case 'wrongName':      return 'refused: not issued for its name';
        case 'noCertificate':  return 'refused: showed no certificate';
    }

}


/**
 * How a judgement looks: fine, worth a look, or wrong. A server believed with
 * another certificate than before is worth a look although it was believed -
 * that is exactly what nobody would otherwise notice.
 */
export function outcomeTone(judgement: ServerJudgement): 'ok' | 'warn' | 'bad' {

    if (!judgement.accepted)
        return 'bad';

    return judgement.outcome === 'accepted' && judgement.previously === null ? 'ok' : 'warn';

}
