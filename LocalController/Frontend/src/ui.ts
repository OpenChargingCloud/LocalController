import type { LogLevel } from './api/client';

export function errorMessage(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}

/**
 * Anything on a page that can be typed into or pressed.
 *
 * Kept as one type because the only thing wanted of them here is that they can
 * all be switched off and on again.
 */
type Control = HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement | HTMLButtonElement;

/** What a page says while it is telling the local controller. */
export const beingSaved = 'Saving ...';

/**
 * Hold a part of a page still while the local controller is being told, and
 * say so.
 *
 * Measured on the vehicle's web interface, over a link that took two seconds
 * to answer: somebody pressed Save and carried on working, as anybody does when
 * nothing has happened yet. The answer arrived, the page redrew itself from
 * it, and what had been typed in the meantime was simply gone - and the page
 * said "Saved, and in effect.". Holding it still makes that impossible rather
 * than unlikely: what cannot be typed in those two seconds cannot be thrown
 * away by the answer.
 *
 * Controls that were already switched off stay off afterwards: a role that may
 * look but not change must not be handed a live form by a save that failed.
 */
export async function whileSaving<T>(Page:    HTMLElement,
                                     Saying:  HTMLElement | null,
                                     Doing:   () => Promise<T>): Promise<T> {

    const controls      = [...Page.querySelectorAll<Control>('input, select, textarea, button')];
    const alreadyOff    = new Set(controls.filter(control => control.disabled));

    for (const control of controls)
        control.disabled = true;

    if (Saying !== null)
        Saying.textContent = beingSaved;

    try
    {
        return await Doing();
    }
    finally
    {
        for (const control of controls)
            if (!alreadyOff.has(control))
                control.disabled = false;

        // Whatever happened, it is no longer happening. What it turned into -
        // "Saved", or a sentence about why not - is the page's to say.
        if (Saying !== null)
            Saying.textContent = '';
    }

}

/** Only same-site paths may be used as a "next" target after signing in. */
export function safeNext(value: string | null): string | null {
    return value !== null && value.startsWith('/') && !value.startsWith('//')
               ? value
               : null;
}

/** Read a form field as a trimmed string. */
export function field(form: HTMLFormElement, name: string, trim = true): string {
    const value = String(new FormData(form).get(name) ?? '');
    return trim ? value.trim() : value;
}


/** Whether one box of a form is ticked. */
export function isChecked(form: HTMLFormElement, name: string): boolean {
    return new FormData(form).get(name) !== null;
}

// Times

/**
 * The formatters, made once.
 *
 * toLocaleTimeString builds one of these on every call, and the log page calls
 * it once per line: 234 ms of a 597 ms redraw at 1959 entries went on the
 * clock alone, against 63 ms with the formatter kept. It reads the browser's
 * locale when the page loads, which is the one moment it can change.
 */
const timeOfDay = new Intl.DateTimeFormat([], {
                          hour:                    '2-digit',
                          minute:                  '2-digit',
                          second:                  '2-digit',
                          fractionalSecondDigits:  3,
                          hour12:                  false
                      });

const wholeMoment = new Intl.DateTimeFormat([], { dateStyle: 'medium', timeStyle: 'medium' });

/** The time of day with milliseconds - the column in front of every log line. */
export function formatTime(iso: string): string {

    const date = new Date(iso);

    if (Number.isNaN(date.getTime()))
        return iso;

    return timeOfDay.format(date);

}

/** The whole moment, for the title of a log line and for the details. */
export function formatTimestamp(iso: string): string {

    const date = new Date(iso);

    return Number.isNaN(date.getTime())
               ? iso
               : wholeMoment.format(date) +
                 `.${String(date.getMilliseconds()).padStart(3, '0')}`;

}

/** How long ago, in the words somebody would use. */
export function formatSince(iso: string): string {

    const then = new Date(iso).getTime();

    if (Number.isNaN(then))
        return iso;

    const seconds = Math.max(0, Math.round((Date.now() - then) / 1000));

    if (seconds <   60)  return `${seconds}s ago`;
    if (seconds < 3600)  return `${Math.round(seconds /    60)}m ago`;
    if (seconds < 86400) return `${Math.round(seconds /  3600)}h ago`;

    return `${Math.round(seconds / 86400)}d ago`;

}


// Values of the configuration page

/**
 * One value of the configuration, as a line of text. Everything the local controller
 * sends is rendered, whether this page knew about it or not - so a new field
 * on the server shows up here without a change.
 */
export function formatValue(value: unknown): string {

    if (value === null || value === undefined)
        return '-';

    if (typeof value === 'boolean')
        return value ? 'yes' : 'no';

    if (Array.isArray(value))
        return value.length === 0 ? '-' : value.map(formatValue).join(', ');

    if (typeof value === 'object')
        return JSON.stringify(value);

    // A field the local controller left empty is a field with nothing in it, and an
    // empty cell reads as a page that failed to render.
    const text = String(value);

    if (text.trim().length === 0)
        return '-';

    // The local controller writes its times in ISO 8601, which is the right thing to
    // send and the wrong thing to read.
    if (isTimestamp(text))
        return formatTimestamp(text);

    return text;

}

/** Whether a string is an ISO 8601 moment, as the local controller writes them. */
function isTimestamp(text: string): boolean {
    return /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/.test(text) &&
           !Number.isNaN(new Date(text).getTime());
}

/**
 * The words that are not words: an acronym the local controller sends in lower case
 * belongs on the page in the case people write it in.
 */
const acronyms = new Map<string, string>([
    ['id',    'ID'],
    ['url',   'URL'],
    ['uri',   'URI'],
    ['http',  'HTTP'],
    ['https', 'HTTPS'],
    ['api',   'API'],
    ['os',    'OS'],
    ['nts',   'NTS'],
    ['ntp',   'NTP'],
    ['dns',   'DNS'],
    ['tls',   'TLS'],
    ['udp',   'UDP'],
    ['edns',  'EDNS'],
    ['ttl',   'TTL'],
    ['aead',  'AEAD'],
    ['ocpp',  'OCPP'],
    ['csms',  'CSMS'],
    ['evse',  'EVSE'],
    ['evses', 'EVSEs'],
    ['ke',    'KE'],
    ['aeadalgorithms', 'AEAD algorithms']
]);

/** "frontendFiles" reads better as "Frontend files". */
export function humanizeKey(key: string): string {

    const words = key.replace(/([a-z0-9])([A-Z])/g, '$1 $2').
                      replace(/_/g, ' ').
                      split(' ').
                      filter(word => word.length > 0);

    return words.map((word, index) => {

               const acronym = acronyms.get(word.toLowerCase());

               if (acronym !== undefined)
                   return acronym;

               // Only the first word is capitalised: "Server name", not
               // "Server Name" - this is a label, not a headline.
               return index === 0
                          ? word.charAt(0).toUpperCase() + word.slice(1)
                          : word.toLowerCase();

           }).join(' ');

}


// Log levels

/** Whether a level is at least as loud as another. */
export function isAtLeast(level: LogLevel, minimum: LogLevel): boolean {

    const order: LogLevel[] = ['debug', 'info', 'notice', 'warning', 'error', 'critical'];

    return order.indexOf(level) >= order.indexOf(minimum);

}
