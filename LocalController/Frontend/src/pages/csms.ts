import { api, type Certificate, type CSMSConfiguration } from '../api/client';
import { auth } from '../auth';
import { toURL } from '@node/basePath';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, formatTimestamp, isChecked, numberField } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * What is said of a line that is a connection's to say, and not this page's:
 * a controller set up before its lines were connections may still say them
 * in its "csms" section, and they are neither shown here nor sent from here.
 */
const saidByAConnection: readonly string[] = [ 'url', 'securityProfile', 'httpVersion' ];

/**
 * The charging station management system above this local controller: whether
 * it reports to one at all, and how every line up to it is kept - its pings,
 * how soon it is dialled again, and the TLS identity it holds up where a line
 * names none of its own.
 *
 * The lines themselves are connections, as a charging station's are: where
 * each goes, what it signs in with and how it knows its server are written
 * down on the Connections page, the credentials on the Authentication page
 * and the keys on the Client keys page. This page says where to find them.
 *
 * Drawn by view.ts: a draw changes only what differs, so that what is typed
 * into its form - and its focus - outlives a draw.
 */
export const csmsPage: Page = {

    title: 'CSMS connection',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/csms',
            title:     'CSMS connection',
            subtitle:  'The charging station management system this local controller reports to.',
            actions:   reloadButton(() => reload())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('csms', 'edit');

        let cancelled = false;
        let csms: CSMSConfiguration | null = null;

        /**
         * The TLS identities of the certificate store, to choose the one a
         * line holds up - or null where this person may not read the store,
         * and the choice is shown but not offered.
         */
        let identities: Certificate[] | null = null;


        function draw(): void {

            if (csms === null)
                return;

            const settings = csms;
            const waiting  = settings.state.waitingForARestart.filter(name => !saidByAConnection.includes(name));

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at this', 'change it')}
                    </div>
                `}

                ${waiting.length === 0 ? nothing : html`
                    <div class="notice warn" id="waiting-for-a-restart">
                        <strong>Saved, but not in effect.</strong> ${waiting.join(', ')}
                        ${waiting.length === 1 ? 'was' : 'were'} changed, and a connection that is already
                        dialled cannot be re-aimed. It takes effect when this local controller is restarted.
                    </div>
                `}

                <div class="cards">
                    ${stateCard(settings)}
                    ${settingsCard(settings)}
                </div>
            `);

        }


        function stateCard(settings: CSMSConfiguration): TemplateResult {

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-satellite-dish"></i> Right now</h2>

                    <dl class="kv">

                        <dt>Line</dt>
                        <dd>
                            ${!settings.enabled
                                  ? html`<span class="badge">switched off</span>`
                                  : settings.state.connected
                                        ? html`<span class="badge ok">connected</span>`
                                        : html`<span class="badge warn">not connected</span>`}
                        </dd>

                        ${settings.state.connectedSince ? html`
                            <dt>Since</dt>
                            <dd>${formatTimestamp(settings.state.connectedSince)}</dd>
                        ` : nothing}

                        <dt>Lines written down</dt>
                        <dd id="lines">${settings.state.connections}</dd>

                    </dl>

                    ${settings.state.lastProblem === null ? nothing : html`
                        <div class="notice warn">${settings.state.lastProblem}</div>
                    `}

                    ${settings.enabled && settings.state.connections === 0 ? html`
                        <div class="notice warn" id="no-line">
                            This local controller is meant to report to a CSMS but no line up to one is written down.
                        </div>
                    ` : nothing}

                    <p class="hint" id="lines-are-connections">
                        The lines themselves - where each goes, what it signs in with and how it knows its server - are
                        written down under <a href="${toURL('/configuration/connections')}">Connections</a>.
                    </p>

                </section>
            `;

        }


        function settingsCard(settings: CSMSConfiguration): TemplateResult {

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-plug"></i> How its lines are kept</h2>

                    <form id="connection-form" class="form-stack" @submit=${saveSettings}>

                        <label class="switch">
                            <input type="checkbox" name="enabled" ?checked=${settings.enabled}
                                   ?disabled=${!mayChange} />
                            <span>Report to a CSMS</span>
                        </label>

                        ${identityField(settings)}

                        <label>Ping every
                            <input type="number" name="pingEvery" min="5" max="3600"
                                   value="${settings.pingEvery}" ?disabled=${!mayChange} />
                            <span class="hint">Seconds. How a CSMS that quietly went away is noticed.</span>
                        </label>

                        <label>Dial again after
                            <input type="number" name="reconnectInitialDelay" min="1" max="600"
                                   value="${settings.reconnectInitialDelay}" ?disabled=${!mayChange} />
                            <span class="hint">
                                Seconds before the first attempt. The wait doubles after each failure, up to
                                ${settings.reconnectMaxDelay} seconds.
                            </span>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save</button>
                            <span id="connection-note" class="form-note"></span>
                            <span id="connection-error" class="form-error" role="alert"></span>
                        </div>

                    </form>

                    <dl class="kv" id="as-the-file-says">

                        <dt>Speaks</dt>
                        <dd>${settings.subprotocols.join(', ')}</dd>

                        ${settings.nextHopNodeId ? html`
                            <dt>Knows the CSMS as</dt>
                            <dd>${settings.nextHopNodeId}</dd>
                        ` : nothing}

                        ${settings.networkingMode ? html`
                            <dt>Networking mode</dt>
                            <dd>${settings.networkingMode}</dd>
                        ` : nothing}

                        ${settings.minimumTLSVersion ? html`
                            <dt>TLS, at least</dt>
                            <dd>${settings.minimumTLSVersion}</dd>
                        ` : nothing}

                        <dt>Asks whether a certificate was revoked</dt>
                        <dd>${settings.checkCertificateRevocation ? 'yes' : 'no'}</dd>

                        <dt>Waits for an answer</dt>
                        <dd>${settings.requestTimeout} seconds</dd>

                    </dl>

                </section>
            `;

        }


        /**
         * The TLS identity a line over TLS holds up where its connection
         * names no client key of its own: chosen from the store's identities,
         * and what the controller says about the one that is chosen - that
         * it is missing, or switched off, before a dialling finds out.
         *
         * Offered only to somebody who may read the store; to anybody else the
         * one chosen is named, and the choice is left alone when they save.
         */
        function identityField(settings: CSMSConfiguration): TemplateResult {

            const chosen = settings.clientCertificateIs;
            const state  = chosen === undefined
                               ? nothing
                               : chosen.missing
                                     ? html`<div class="notice warn">The identity ${chosen.id} chosen here is not in the certificate store.</div>`
                                     : chosen.usable === false
                                           ? html`<div class="notice warn">${chosen.label} is switched off, or not valid today - no line can hold it up.</div>`
                                           : nothing;

            // None is drawn as chosen whenever no identity offered is the one
            // chosen - also where that one is missing from the store, or is no
            // identity - as the browser would choose it anyway: a select with
            // no option drawn as selected counts as typed into - see
            // typedSinceDrawn - and leaving the page asked about a draft
            // nobody had begun.
            const offered = identities?.some(identity => identity.id === chosen?.id) ?? false;

            return html`
                <label>TLS identity, where a line names no client key
                    ${identities === null
                          ? html`<input type="text" value="${chosen ? `${chosen.label ?? ''} (${chosen.id})` : 'none'}" disabled />
                                 <span class="hint">Choosing one takes reading the certificate store, which this account may not.</span>`
                          : html`<select name="clientCertificate" ?disabled=${!mayChange}>
                                     <option value="" ?selected=${!offered}>none</option>
                                     ${repeat(identities, identity => identity.id, identity => html`
                                         <option value="${identity.id}" ?selected=${chosen?.id === identity.id}>
                                             ${identity.label} (${identity.id})${identity.usable ? '' : ' - not usable'}
                                         </option>
                                     `)}
                                 </select>
                                 <span class="hint">
                                     What this controller presents in TLS, with its private key, on a line whose
                                     connection names no client key of its own: one of its
                                     <a href="${toURL('/configuration/identities')}">identities</a>, imported on the
                                     certificate store's page. The CSMS knows it by the last segment of the address,
                                     as every OCPP client says who it is.
                                 </span>`}
                </label>
                ${state}
            `;

        }


        function saveSettings(event: SubmitEvent): void {

            event.preventDefault();

            const form     = event.currentTarget as HTMLFormElement;
            const identity = form.querySelector<HTMLSelectElement>('select[name="clientCertificate"]');

            void save(form, {
                enabled:                isChecked(form, 'enabled'),
                pingEvery:              numberField(form, 'pingEvery'),
                reconnectInitialDelay:  numberField(form, 'reconnectInitialDelay'),
                // Left out where it was not offered, which leaves it alone;
                // none is said as null.
                ...(identity === null ? {} : { clientCertificate: identity.value.length > 0 ? identity.value : null })
            });

        }


        async function save(form: HTMLFormElement, update: Parameters<typeof api.csms.save>[0]): Promise<void> {

            const note  = content.querySelector<HTMLElement>('#connection-note');
            const error = content.querySelector<HTMLElement>('#connection-error');

            if (note)   note.textContent  = 'Saving ...';
            if (error)  error.textContent = '';

            try
            {

                csms = await api.csms.save(update);

                if (cancelled)
                    return;

                if (note)
                    note.textContent = '';

                draw();

                // A draw leaves a form as it is typed into; this one was
                // saved, so it goes back to what it says now - the answer.
                form.reset();

            }
            catch (problem)
            {

                if (cancelled)
                    return;

                if (note)   note.textContent  = '';
                if (error)  error.textContent = errorMessage(problem);

                // What was refused is not what is running, so the page goes
                // back to saying what is true - a draw keeps what is typed
                // into its form, to be put right, and why it was refused.
                await load();

            }

        }


        /**
         * The page as the controller has it now, drawn over the page as it
         * is - what is typed into its form kept, as a draw keeps it. Reload
         * empties it itself.
         */
        async function load(): Promise<void> {

            try
            {

                // The store beside the settings: it only offers the identities
                // to choose from, and a store this person may not read is no
                // reason to show no page.
                const [settings, kept] = await Promise.all([
                                             api.csms.get(),
                                             auth.can('certificates', 'read')
                                                 ? api.certificates.get().then(store => store.certificates.tlsIdentity ?? [], () => null)
                                                 : Promise.resolve(null)
                                         ]);

                if (cancelled)
                    return;

                csms       = settings;
                identities = kept;

                draw();

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`<div class="error-box">${errorMessage(problem)}</div>`);
            }

        }


        /**
         * Loaded anew - Reload - is what the controller has, the form too,
         * which a draw on its own would leave as typed.
         */
        async function reload(): Promise<void> {

            await load();

            if (!cancelled)
                content.querySelectorAll('form').forEach(form => form.reset());

        }


        // The form is a draft until it is saved.
        const release = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        return () => { cancelled = true; release(); };

    }

};
