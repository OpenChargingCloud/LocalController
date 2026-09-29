import { api, type KnownServer, type ServerJudgement } from '../api/client';
import { auth } from '../auth';
import { html, must, type HTMLFragment } from '@node/html';
import { formatValue } from '../ui';
import { offersOf, outcomeText, outcomeTone, pinsText, readPins, shortFingerprint, type PinsDraft, type StoreOffers } from './pins';

/**
 * The certificate of a server this local controller connects to - a time
 * server, a name server over TLS or HTTPS - as the NTS page and the DNS page
 * show it: what the controller made of it, and the fields that say what it is
 * held to.
 *
 * One module for both pages because it is one question on both: a server that
 * has to be this one, and not merely one a certificate authority vouches for.
 * The decisions are in pins.ts; this only draws them. The vehicle's pages draw
 * them the same way, from a module the vehicle calls serverCertificates.ts -
 * which here is the page of the charging station port's own certificates.
 */


/**
 * The day of a moment, in the browser's words: since when a certificate is
 * believed is a question of days, and the controller's timestamps say it to
 * the ten-millionth of a second.
 */
const day = new Intl.DateTimeFormat([], { dateStyle: 'medium' });

function dayOf(iso: string): string {

    const date = new Date(iso);

    return Number.isNaN(date.getTime()) ? iso : day.format(date);

}


/** Where a server's dialog is opened, and what it can offer to be picked. */
export interface PinsContext {
    /** "nts" or "dns": which roots and server certificates of the store are for it. */
    service:  'nts' | 'dns';
    /**
     * The server, and the certificate and root it showed the last time it was
     * asked - which is what a pin is most often written down from.
     */
    shown:    { name: string; certificate: string | null; root: string | null } | null;
    /** What the certificate store keeps for this service, where this person may read it. */
    offers:   StoreOffers | null;
}


/**
 * What the certificate store keeps for a service, or null where the person
 * signed in may not read it or it cannot be read - the fields are there
 * either way, and a fingerprint can always be typed.
 */
export async function storeOffers(service: 'nts' | 'dns'): Promise<StoreOffers | null> {

    if (!auth.can('certificates', 'read'))
        return null;

    try
    {
        return offersOf(await api.certificates.get(), service);
    }
    catch
    {
        return null;
    }

}


/** A fingerprint in a row: its first digits, the whole of it where the pointer rests. */
function fingerprintView(fingerprint: string, nameOf?: (fingerprint: string) => string | undefined): HTMLFragment {

    const name = nameOf?.(fingerprint);

    return html`<code class="fingerprint-short" title="SHA-256: ${fingerprint}">${name ?? shortFingerprint(fingerprint)}</code>`;

}


/**
 * What the controller made of a server's certificate the last time it looked,
 * as chips: the verdict, and what was news about it. Where it has not looked
 * since it started, what it was last believed with before.
 */
export function certificateVerdictView(judgement: ServerJudgement | null | undefined,
                                       known:     KnownServer     | null | undefined): HTMLFragment {

    if (judgement === null || judgement === undefined)
        return known
                   ? html`<span class="muted" title="What it was last believed with is known since then, and another one is noticed.">
                              not looked at since the start; its certificate known since ${dayOf(known.since)}
                          </span>`
                   : html`<span class="muted">no certificate seen yet</span>`;

    return html`
        <span class="chip ${outcomeTone(judgement)}"
              title="${formatValue(judgement.at)}">${outcomeText(judgement.outcome)}</span>
        ${judgement.previously
              ? html`<span class="chip warn" title="It showed ${judgement.previously.certificate} before">
                         another certificate than since ${dayOf(judgement.previously.since)}
                     </span>`
              : ''}
        ${judgement.learned
              ? html`<span class="chip ok">held to this ${judgement.learned} from now on</span>`
              : ''}
        ${judgement.anchoredBy
              ? html`<span class="muted">validated by this local controller's root ${judgement.anchoredBy}</span>`
              : ''}
    `;

}


/**
 * The certificate and the root a server showed last - or, where the
 * controller has not looked since it started, the ones it was last believed
 * with.
 */
export function shownView(judgement: ServerJudgement | null | undefined,
                          known:     KnownServer     | null | undefined,
                          nameOf?:   (fingerprint: string) => string | undefined): HTMLFragment {

    const certificate  = judgement?.certificate ?? known?.certificate ?? null;
    const root         = judgement?.root        ?? known?.root        ?? null;

    if (certificate === null)
        return html``;

    return html`
        <span class="muted">
            certificate ${fingerprintView(certificate, nameOf)}${root ? html`, root ${fingerprintView(root, nameOf)}` : ''}
        </span>
    `;

}


/**
 * What a server is held to, in a line - or nothing, where it is held to
 * nothing beyond what every server is held to.
 *
 * @param unsaved  whether this is the page's draft and not yet what the
 *                 controller holds it to.
 */
export function heldToView(draft:    PinsDraft,
                           nameOf?:  (fingerprint: string) => string | undefined,
                           unsaved = false): HTMLFragment {

    const text = pinsText(draft, nameOf);

    if (text === null)
        return unsaved ? html`<span class="muted">held to no fingerprint <em>once saved</em></span>` : html``;

    return html`<span class="held-to">Held to ${text}${unsaved ? html` <em>once saved</em>` : ''}</span>`;

}


/**
 * The fields that say what a server is held to, for the dialog of a time
 * server or of a name server.
 *
 * Fingerprints are typed or pasted one to a line, and the ones there are
 * better sources for - what the server showed last, what the certificate
 * store keeps - can be added with a click rather than copied by hand. A
 * time server's certificate is evidence for the time this local controller
 * keeps, so the list of what a mismatch comes to says where each goes.
 */
export function pinsFieldset(draft: PinsDraft, context: PinsContext): HTMLFragment {

    const what     = context.service === 'nts' ? 'time server' : 'name server';
    const evidence = context.service === 'nts';
    const shown    = context.shown;
    const offers   = context.offers;

    return html`
        <fieldset class="pins">

            <legend>What its certificate is held to</legend>

            <p class="hint">
                Beyond what every ${what} is held to: a certificate issued for its name, whose chain ends
                at a root this machine or this local controller trusts. Nothing here, and any such
                certificate is believed - and a change of it is written into the log all the same.
            </p>

            <label>Certificates it may show
                <textarea name="pinCertificates" rows="2" spellcheck="false" autocomplete="off"
                          placeholder="SHA-256 fingerprints, one to a line">${draft.certificates.join('\n')}</textarea>
                <span class="hint">Any one of them: a second one is a renewal written down before it happens.</span>
            </label>

            ${offerView('pinCertificates', shown?.certificate ?? null, draft.certificates,
                        shown === null ? '' : `Add the one ${shown.name} showed`,
                        offers?.certificates ?? [], `... or a server certificate kept for ${context.service.toUpperCase()}`)}

            <label>Roots its chain may end at
                <textarea name="pinRoots" rows="2" spellcheck="false" autocomplete="off"
                          placeholder="SHA-256 fingerprints, one to a line">${draft.roots.join('\n')}</textarea>
                <span class="hint">
                    Any one of them. A root outlives the certificates it issues, so it is the pin a renewal
                    does not break.
                </span>
            </label>

            ${offerView('pinRoots', shown?.root ?? null, draft.roots,
                        shown === null ? '' : `Add the root its chain ended at`,
                        offers?.roots ?? [], `... or a TLS root kept for ${context.service.toUpperCase()}`)}

            <label>When it shows another one
                <select name="pinMismatch">
                    <option value="refuse" ${draft.onMismatch === 'refuse' ? html`selected` : ''}>refuse it</option>
                    <option value="record" ${draft.onMismatch === 'record' ? html`selected` : ''}>use it all the same, and write that into the metrological log</option>
                    <option value="accept" ${draft.onMismatch === 'accept' ? html`selected` : ''}>${evidence
                        ? 'use it all the same - for a time server, that goes into the metrological log either way'
                        : 'use it all the same, and write that into the log'}</option>
                </select>
                <span class="hint">
                    Only where it is held to a fingerprint, or learns one below. Whatever it comes to is written
                    into the log, tagged <code>security</code>.
                </span>
            </label>

            <label>Trust on first use
                <select name="pinLearn">
                    <option value=""            ${draft.trustOnFirstUse === null          ? html`selected` : ''}>no</option>
                    <option value="root"        ${draft.trustOnFirstUse === 'root'        ? html`selected` : ''}>hold it to the root its chain first ends at</option>
                    <option value="certificate" ${draft.trustOnFirstUse === 'certificate' ? html`selected` : ''}>hold it to the first certificate it is believed with</option>
                </select>
                <span class="hint">
                    Learned the first time it is believed while it is held to none of that kind, and written into
                    its entry - it is in the list above from then on.
                </span>
            </label>

        </fieldset>
    `;

}


/**
 * The ways to add a fingerprint to one of the lists without typing it: the one
 * the server showed, and the ones the certificate store keeps for this service.
 */
function offerView(list:          'pinCertificates' | 'pinRoots',
                   shown:         string | null,
                   already:       string[],
                   shownLabel:    string,
                   kept:          { thumbprint: string; label: string; id: string }[],
                   keptLabel:     string): HTMLFragment {

    const offerShown = shown !== null && shownLabel.length > 0 && !already.includes(shown);

    if (!offerShown && kept.length === 0)
        return html``;

    return html`
        <div class="pin-offers">
            ${offerShown
                  ? html`<button type="button" class="btn small" data-pin-add="${list}" data-fingerprint="${shown}"
                                 title="${shown}">${shownLabel}</button>`
                  : ''}
            ${kept.length > 0
                  ? html`<select data-pin-pick="${list}" aria-label="${keptLabel}">
                             <option value="">${keptLabel}</option>
                             ${kept.map(entry => html`<option value="${entry.thumbprint}">${entry.label} (${entry.id})</option>`)}
                         </select>`
                  : ''}
        </div>
    `;

}


/**
 * Make the offers of a dialog's pin fields add what they offer: to the end of
 * their list, once.
 */
export function wirePinsFieldset(dialog: HTMLElement): void {

    const add = (list: string, fingerprint: string): void => {

        const area = dialog.querySelector<HTMLTextAreaElement>(`textarea[name="${list}"]`);

        if (area === null || fingerprint.length === 0)
            return;

        const lines = area.value.split('\n').map(line => line.trim()).filter(line => line.length > 0);

        if (!lines.some(line => line.toLowerCase() === fingerprint.toLowerCase()))
            area.value = [ ...lines, fingerprint ].join('\n');

    };

    dialog.addEventListener('click', event => {

        const button = (event.target as HTMLElement).closest<HTMLButtonElement>('[data-pin-add]');

        if (button === null)
            return;

        add(button.dataset.pinAdd!, button.dataset.fingerprint ?? '');

        button.hidden = true;

    });

    dialog.addEventListener('change', event => {

        const select = (event.target as HTMLElement).closest<HTMLSelectElement>('[data-pin-pick]');

        if (select === null)
            return;

        add(select.dataset.pinPick!, select.value);

        select.value = '';

    });

}


/** What a dialog's pin fields say, or what is wrong with them. */
export function readPinsFieldset(form: HTMLFormElement): { draft: PinsDraft; error?: undefined } | { draft?: undefined; error: string } {

    return readPins({
        certificates:     must<HTMLTextAreaElement>(form, 'textarea[name="pinCertificates"]').value,
        roots:            must<HTMLTextAreaElement>(form, 'textarea[name="pinRoots"]').value,
        onMismatch:       must<HTMLSelectElement>  (form, 'select[name="pinMismatch"]').value,
        trustOnFirstUse:  must<HTMLSelectElement>  (form, 'select[name="pinLearn"]').value
    });

}


/**
 * What was made of one certificate in a test, step by step: the verdict, what
 * it showed, and every step that led there.
 */
export function judgementView(judgement: ServerJudgement,
                              nameOf?:   (fingerprint: string) => string | undefined): HTMLFragment {

    const root = judgement.root === null ? '-' : `${nameOf?.(judgement.root) ?? ''}${nameOf?.(judgement.root) ? ' - ' : ''}${judgement.root}`;

    return html`
        <div class="judgement">

            <div class="judgement-head">
                <strong>${judgement.server}</strong>
                <span class="chip ${outcomeTone(judgement)}">${outcomeText(judgement.outcome)}</span>
            </div>

            <div class="kv-list">
                <div class="kv"><span class="k">Certificate</span><span class="v">${judgement.certificate ?? '-'}</span></div>
                <div class="kv"><span class="k">Root</span><span class="v">${root}</span></div>
                ${judgement.anchoredBy
                      ? html`<div class="kv"><span class="k">Validated by</span><span class="v">this local controller's root ${judgement.anchoredBy}</span></div>`
                      : ''}
            </div>

            ${(judgement.steps ?? []).length === 0
                  ? ''
                  : html`
                      <ol class="test-log">
                          ${(judgement.steps ?? []).map(step => html`
                              <li class="level-${step.level}">
                                  <span class="at">${step.level}</span>
                                  <span class="text">${step.text}</span>
                              </li>
                          `)}
                      </ol>
                  `}

        </div>
    `;

}
