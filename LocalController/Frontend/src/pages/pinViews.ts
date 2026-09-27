import type { KnownServer, ServerJudgement } from '../api/client';
import { html, must, type HTMLFragment } from '../html';
import { formatValue } from '../ui';
import { outcomeText, outcomeTone, pinsText, readPins, shortFingerprint, type PinsDraft } from './pins';

/**
 * The certificate of a server this local controller connects to - a time
 * server, a name server over TLS or HTTPS - as the NTS page and the DNS page
 * show it: what the controller made of it, and the fields that say what it is
 * held to.
 *
 * One module for both pages because it is one question on both: a server that
 * has to be this one, and not merely one a certificate authority vouches for.
 * The decisions are in pins.ts; this only draws them. The vehicle's pages draw
 * them the same way, and offer fingerprints from a certificate store besides,
 * which a local controller does not have.
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


/** Where a server's dialog is opened, and what it can offer to be added. */
export interface PinsContext {
    /** "nts" or "dns": which server this is, for the words. */
    service:  'nts' | 'dns';
    /**
     * The server, and the certificate and root it showed the last time it was
     * asked - which is what a pin is most often written down from.
     */
    shown:    { name: string; certificate: string | null; root: string | null } | null;
}


/** A fingerprint in a row: its first digits, the whole of it where the pointer rests. */
function fingerprintView(fingerprint: string): HTMLFragment {
    return html`<code class="fingerprint-short" title="SHA-256: ${fingerprint}">${shortFingerprint(fingerprint)}</code>`;
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
                          known:     KnownServer     | null | undefined): HTMLFragment {

    const certificate  = judgement?.certificate ?? known?.certificate ?? null;
    const root         = judgement?.root        ?? known?.root        ?? null;

    if (certificate === null)
        return html``;

    return html`
        <span class="muted">
            certificate ${fingerprintView(certificate)}${root ? html`, root ${fingerprintView(root)}` : ''}
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
                           unsaved = false): HTMLFragment {

    const text = pinsText(draft);

    if (text === null)
        return unsaved ? html`<span class="muted">held to no fingerprint <em>once saved</em></span>` : html``;

    return html`<span class="held-to">Held to ${text}${unsaved ? html` <em>once saved</em>` : ''}</span>`;

}


/**
 * The fields that say what a server is held to, for the dialog of a time
 * server or of a name server.
 *
 * Fingerprints are typed or pasted one to a line, and the one there is the
 * best source for - what the server showed last - can be added with a click
 * rather than copied by hand. A time server's certificate is evidence for the
 * time this local controller keeps, so the list of what a mismatch comes to
 * says where each goes.
 */
export function pinsFieldset(draft: PinsDraft, context: PinsContext): HTMLFragment {

    const what     = context.service === 'nts' ? 'time server' : 'name server';
    const evidence = context.service === 'nts';
    const shown    = context.shown;

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
                        shown === null ? '' : `Add the one ${shown.name} showed`)}

            <label>Roots its chain may end at
                <textarea name="pinRoots" rows="2" spellcheck="false" autocomplete="off"
                          placeholder="SHA-256 fingerprints, one to a line">${draft.roots.join('\n')}</textarea>
                <span class="hint">
                    Any one of them. A root outlives the certificates it issues, so it is the pin a renewal
                    does not break.
                </span>
            </label>

            ${offerView('pinRoots', shown?.root ?? null, draft.roots,
                        shown === null ? '' : `Add the root its chain ended at`)}

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
 * The way to add a fingerprint to one of the lists without typing it: the one
 * the server showed, where it is not in the list already.
 */
function offerView(list:        'pinCertificates' | 'pinRoots',
                   shown:       string | null,
                   already:     string[],
                   shownLabel:  string): HTMLFragment {

    if (shown === null || shownLabel.length === 0 || already.includes(shown))
        return html``;

    return html`
        <div class="pin-offers">
            <button type="button" class="btn small" data-pin-add="${list}" data-fingerprint="${shown}"
                    title="${shown}">${shownLabel}</button>
        </div>
    `;

}


/**
 * Make the offers of a dialog's pin fields add what they offer: to the end of
 * their list, once.
 */
export function wirePinsFieldset(dialog: HTMLElement): void {

    dialog.addEventListener('click', event => {

        const button = (event.target as HTMLElement).closest<HTMLButtonElement>('[data-pin-add]');

        if (button === null)
            return;

        const area        = dialog.querySelector<HTMLTextAreaElement>(`textarea[name="${button.dataset.pinAdd}"]`);
        const fingerprint = button.dataset.fingerprint ?? '';

        if (area === null || fingerprint.length === 0)
            return;

        const lines = area.value.split('\n').map(line => line.trim()).filter(line => line.length > 0);

        if (!lines.some(line => line.toLowerCase() === fingerprint.toLowerCase()))
            area.value = [ ...lines, fingerprint ].join('\n');

        button.hidden = true;

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
export function judgementView(judgement: ServerJudgement): HTMLFragment {

    return html`
        <div class="judgement">

            <div class="judgement-head">
                <strong>${judgement.server}</strong>
                <span class="chip ${outcomeTone(judgement)}">${outcomeText(judgement.outcome)}</span>
            </div>

            <div class="kv-list">
                <div class="kv"><span class="k">Certificate</span><span class="v">${judgement.certificate ?? '-'}</span></div>
                <div class="kv"><span class="k">Root</span><span class="v">${judgement.root ?? '-'}</span></div>
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
