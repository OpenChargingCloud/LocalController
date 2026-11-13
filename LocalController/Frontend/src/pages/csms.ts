import { api, type Certificate, type CSMSConfiguration, type CSMSKeyPins } from '../api/client';
import { auth } from '../auth';
import { toURL } from '@node/basePath';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, field, formatTimestamp, isChecked, numberField } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * The charging station management system above this local controller.
 *
 * The mirror image of the charging station page: there this controller is a
 * server that others sign in to, here it is a client that signs in to somebody
 * else. The one asymmetry worth knowing is about secrets - the passwords of the
 * stations below are hashed and cannot be read back, while what this controller
 * signs in with upwards has to stay readable, because saying it is the whole
 * point. Neither ever reaches this page.
 *
 * Drawn by view.ts: a draw changes only what differs, so that what is typed
 * into one of its two forms - and its focus - outlives the other being saved.
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
         * The TLS identities of the certificate store, to choose the one
         * security profile 3 signs in with - or null where this person may
         * not read the store, and the choice is shown but not offered.
         */
        let identities: Certificate[] | null = null;


        function draw(): void {

            if (csms === null)
                return;

            const settings = csms;
            const waiting  = settings.state.waitingForARestart;

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at this', 'change it')}
                    </div>
                `}

                ${waiting.length === 0 ? nothing : html`
                    <div class="notice warn">
                        <strong>Saved, but not in effect.</strong> ${waiting.join(', ')}
                        ${waiting.length === 1 ? 'was' : 'were'} changed, and a connection that is already
                        dialled cannot be re-aimed. It takes effect when this local controller is restarted.
                    </div>
                `}

                <div class="cards">
                    ${stateCard(settings)}
                    ${connectionCard(settings)}
                    ${credentialsCard(settings)}
                    ${settings.keyPins ? keyPinsCard(settings.keyPins) : nothing}
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

                        <dt>Signs in as</dt>
                        <dd>${settings.securityProfile === 3
                                  ? settings.clientCertificateIs
                                        ? html`its certificate <strong>${settings.clientCertificateIs.label ?? settings.clientCertificateIs.id}</strong>`
                                        : html`<em>no TLS identity chosen</em>`
                                  : settings.credentials.username ?? html`<em>nothing set</em>`}</dd>

                    </dl>

                    ${settings.state.lastProblem === null ? nothing : html`
                        <div class="notice warn">${settings.state.lastProblem}</div>
                    `}

                    ${settings.enabled && settings.securityProfile !== 3 && !settings.state.hasCredentials ? html`
                        <div class="notice warn">
                            This local controller is meant to report to a CSMS but has nothing to sign in with.
                            Set it below.
                        </div>
                    ` : nothing}

                </section>
            `;

        }


        function connectionCard(settings: CSMSConfiguration): TemplateResult {

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-plug"></i> Where it reports to</h2>

                    <form id="connection-form" class="form-stack" @submit=${saveConnection}>

                        <label class="switch">
                            <input type="checkbox" name="enabled" ?checked=${settings.enabled}
                                   ?disabled=${!mayChange} />
                            <span>Report to a CSMS</span>
                        </label>

                        <label>Address
                            <input type="text" name="url" maxlength="400" value="${settings.url ?? ''}"
                                   placeholder="wss://csms.example.org/ocpp/lc001"
                                   ?disabled=${!mayChange} />
                            <span class="hint">
                                A WebSocket address. <code>wss://</code> for security profiles 2 and 3,
                                <code>ws://</code> only for profile 1.
                            </span>
                        </label>

                        <label>Security profile
                            <select name="securityProfile" ?disabled=${!mayChange}>
                                <option value="1" ?selected=${settings.securityProfile === 1}>
                                    1 - a password, unencrypted
                                </option>
                                <option value="2" ?selected=${settings.securityProfile === 2}>
                                    2 - a password over TLS
                                </option>
                                <option value="3" ?selected=${settings.securityProfile === 3}>
                                    3 - a client certificate over TLS
                                </option>
                            </select>
                        </label>

                        <div class="notice">
                            <strong>Profile 1 sends the password in the clear.</strong> The line to a backend
                            crosses networks this site does not own, so profile 2 is the least that makes sense
                            outside a laboratory. Profile 3 signs in with a TLS identity of this controller's own,
                            chosen below from its <a href="${toURL('/configuration/identities')}">identities</a>.
                        </div>

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

                </section>
            `;

        }


        /**
         * The TLS identity security profile 3 signs in with: chosen from the
         * store's identities, and what the controller says about the one that
         * is chosen - that it is missing, or switched off, before a dialling
         * finds out.
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
                                           ? html`<div class="notice warn">${chosen.label} is switched off, or not valid today - profile 3 cannot sign in with it.</div>`
                                           : nothing;

            // None is drawn as chosen whenever no identity offered is the one
            // chosen - also where that one is missing from the store, or is no
            // identity - as the browser would choose it anyway: a select with
            // no option drawn as selected counts as typed into - see
            // typedSinceDrawn - and leaving the page asked about a draft
            // nobody had begun.
            const offered = identities?.some(identity => identity.id === chosen?.id) ?? false;

            return html`
                <label>TLS identity, for security profile 3
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
                                     What this controller presents in TLS, with its private key, imported on the
                                     certificate store's page. The CSMS knows it by the last segment of the address,
                                     as every OCPP client says who it is.
                                 </span>`}
                </label>
                ${state}
            `;

        }


        function credentialsCard(settings: CSMSConfiguration): TemplateResult {

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-key"></i> What it signs in with</h2>

                    <p class="hint">
                        Issued by whoever runs the CSMS and typed in here - unlike the passwords of the charging
                        stations below, which this controller makes up and hashes. This one cannot be hashed:
                        saying it is what it is for. It is kept in ${settings.credentials.file}, readable by its
                        owner alone, and never travels to this page.
                    </p>

                    ${settings.credentials.hasPassword || settings.credentials.hasTOTP ? html`
                        <p>
                            Set for <strong>${settings.credentials.username}</strong>:
                            ${settings.credentials.hasPassword ? html`<span class="badge">password</span> ` : nothing}
                            ${settings.credentials.hasTOTP ? html`<span class="badge">one-time token</span>` : nothing}
                        </p>
                    ` : html`<p class="muted">Nothing set yet.</p>`}

                    <form id="credentials-form" class="form-stack" @submit=${saveCredentials}>

                        <label>Signs in as
                            <input type="text" name="username" maxlength="48"
                                   value="${settings.credentials.username ?? ''}"
                                   placeholder="lc001" ?disabled=${!mayChange} required />
                        </label>

                        <label>Password
                            <input type="text" name="password"
                                   minlength="${settings.credentials.minPasswordLength}" maxlength="64"
                                   placeholder="what the CSMS issued" ?disabled=${!mayChange} />
                        </label>

                        <label>Or a TOTP shared secret
                            <input type="text" name="sharedSecret" maxlength="128"
                                   placeholder="what the CSMS issued" ?disabled=${!mayChange} />
                            <span class="hint">
                                Fill in one of the two. A shared secret cannot be made up here either: the other
                                end has to know it.
                            </span>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save</button>
                            <button type="button" id="credentials-remove" class="btn danger"
                                    ?disabled=${!mayChange || !(settings.credentials.hasPassword || settings.credentials.hasTOTP)}
                                    @click=${() => void forgetCredentials()}>
                                Forget them
                            </button>
                            <span id="credentials-error" class="form-error" role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        /**
         * How the line believes its CSMS beyond what a CA says ("Key Pins and Raw Public Keys for
         * Local Controllers"): by the key pin of its key - configured here, learned on first use and on
         * probation, or confirmed by a pin set the CSMS's signing anchor signed.
         */
        function keyPinsCard(pins: CSMSKeyPins): TemplateResult {

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-thumbtack"></i> How it believes the CSMS</h2>

                    <p class="hint">
                        Over wss://, the CSMS is believed either by its certificate, as a CA vouches for it, or by the key
                        pin of its key and by nothing else of its certificate. A key pin learned on the first connection
                        is on probation: until a pin set signed by one of the signers below confirms it, nothing the CSMS
                        asks is carried out or passed on to a station. Changes apply from the next dial of the line.
                    </p>

                    <p id="keypins-state">
                        ${!pins.overTLS
                              ? html`<span class="badge">not over TLS</span>`
                              : pins.keyPinned
                                    ? html`<span class="badge ok">key-pinned</span>`
                                    : html`<span class="badge">by its certificate</span>`}
                        ${pins.onProbation ? html` <span class="badge warn">on probation</span>` : nothing}
                        ${pins.serverKeyPins.length > 0
                              ? html` <span class="badge">${pins.serverKeyPinState.toLowerCase()}, version ${pins.serverKeyPinsVersion}</span>`
                              : nothing}
                    </p>

                    <dl class="facts">
                        <dt>Key pins</dt>
                        <dd id="keypins-pins">${pins.serverKeyPins.length > 0
                                                    ? html`${repeat(pins.serverKeyPins, pin => pin, pin => html`<code>${pin}</code><br />`)}`
                                                    : html`<em>none</em>`}</dd>
                        ${pins.serverKeyPinLearned ? html`
                            <dt>Learned</dt>
                            <dd id="keypins-learned"><code>${pins.serverKeyPinLearned}</code>
                                ${pins.serverKeyPinLearnedAt ? html`<span class="muted">at ${formatTimestamp(pins.serverKeyPinLearnedAt)}</span>` : nothing}</dd>
                        ` : nothing}
                        ${pins.keyPinShown ? html`
                            <dt>Shown last</dt>
                            <dd><code>${pins.keyPinShown}</code></dd>
                        ` : nothing}
                    </dl>

                    <form id="keypins-form" class="form-stack" @submit=${saveKeyPins}>

                        <label>Key pins
                            <textarea name="serverKeyPins" rows="2" ?disabled=${!mayChange}
                                      placeholder="64 hexadecimal digits each, separated by commas"
                                      .defaultValue=${pins.serverKeyPins.join(', ')}></textarea>
                            <span class="hint">Configured here, not signed. Empty, with trust on first use off: by its certificate again.</span>
                        </label>

                        <label class="checkbox">
                            <input type="checkbox" name="trustOnFirstUse" ?checked=${pins.trustOnFirstUse === 'Key'} ?disabled=${!mayChange} />
                            Learn the key pin of the first key shown, on probation
                        </label>

                        <label>Probation in seconds
                            <input type="number" name="serverKeyPinProbationTime" min="1" max="604800"
                                   value="${pins.serverKeyPinProbationTime}" ?disabled=${!mayChange} />
                        </label>

                        <label>Raw public keys
                            <select name="rawPublicKeys" ?disabled=${!mayChange}>
                                ${[ 'Off', 'Prefer', 'Require' ].map(mode => html`<option value="${mode}" ?selected=${pins.rawPublicKeys === mode}>${mode}</option>`)}
                            </select>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save</button>
                            <span id="keypins-error" class="form-error" role="alert"></span>
                        </div>

                    </form>

                    <h3>Signers of pin sets</h3>

                    ${pins.pinSetSigners.length === 0
                          ? html`<p class="muted" id="keypins-no-signer">None: a pin set sent over OCPP is refused.</p>`
                          : html`
                              <ul class="plain" id="keypins-signers">
                                  ${repeat(pins.pinSetSigners, signer => signer.publicKey, signer => html`
                                      <li>
                                          <strong>${signer.description}</strong> <code>${signer.publicKey}</code>
                                          <button type="button" class="btn small danger" ?disabled=${!mayChange}
                                                  @click=${() => void removeSigner(signer.publicKey, signer.description)}>Remove</button>
                                      </li>
                                  `)}
                              </ul>`}

                    <form id="signer-form" class="form-stack" @submit=${addSigner}>

                        <label>Public key
                            <input type="text" name="publicKey" ?disabled=${!mayChange} required
                                   placeholder="the CSMS's signing key: a point of P-256, P-384 or P-521, base64 or hexadecimal" />
                        </label>

                        <label>Whose
                            <input type="text" name="description" maxlength="100" ?disabled=${!mayChange} required placeholder="The CSMS of the site" />
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn" ?disabled=${!mayChange}>Add</button>
                            <span id="signer-error" class="form-error" role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        function saveKeyPins(event: SubmitEvent): void {

            event.preventDefault();

            const form = event.currentTarget as HTMLFormElement;

            void changeKeyPins(form, () => api.csms.saveKeyPins({
                serverKeyPins:              field(form, 'serverKeyPins'),
                trustOnFirstUse:            isChecked(form, 'trustOnFirstUse') ? 'Key' : 'None',
                serverKeyPinProbationTime:  numberField(form, 'serverKeyPinProbationTime'),
                rawPublicKeys:              (form.querySelector<HTMLSelectElement>('select[name="rawPublicKeys"]')?.value ?? 'Off') as 'Off' | 'Prefer' | 'Require'
            }), '#keypins-error');

        }


        function addSigner(event: SubmitEvent): void {

            event.preventDefault();

            const form = event.currentTarget as HTMLFormElement;

            void changeKeyPins(form, () => api.csms.addPinSetSigner({
                publicKey:    field(form, 'publicKey'),
                description:  field(form, 'description')
            }), '#signer-error');

        }


        async function removeSigner(publicKey: string, description: string): Promise<void> {

            if (!window.confirm(`Believe the pin sets of '${description}' no longer?`))
                return;

            try
            {
                csms = await api.csms.removePinSetSigner(publicKey);
                if (!cancelled)
                    draw();
            }
            catch (problem)
            {
                if (!cancelled)
                    window.alert(errorMessage(problem));
            }

        }


        /**
         * Change how the line believes its CSMS: the answer drawn and the form gone back to it - or, where it
         * was refused, what is typed kept, and why it was said.
         */
        async function changeKeyPins(form: HTMLFormElement, change: () => Promise<CSMSConfiguration>, errorAt: string): Promise<void> {

            const error = content.querySelector<HTMLElement>(errorAt);

            if (error)
                error.textContent = '';

            try
            {

                csms = await change();

                if (cancelled)
                    return;

                draw();
                form.reset();

            }
            catch (problem)
            {
                if (error && !cancelled)
                    error.textContent = errorMessage(problem);
            }

        }


        function saveConnection(event: SubmitEvent): void {

            event.preventDefault();

            const form     = event.currentTarget as HTMLFormElement;
            const identity = form.querySelector<HTMLSelectElement>('select[name="clientCertificate"]');

            void save(form, {
                enabled:                isChecked(form, 'enabled'),
                url:                    field(form, 'url', false),
                securityProfile:        numberField(form, 'securityProfile'),
                pingEvery:              numberField(form, 'pingEvery'),
                reconnectInitialDelay:  numberField(form, 'reconnectInitialDelay'),
                // Left out where it was not offered, which leaves it alone;
                // none is said as null.
                ...(identity === null ? {} : { clientCertificate: identity.value.length > 0 ? identity.value : null })
            });

        }


        function saveCredentials(event: SubmitEvent): void {

            event.preventDefault();

            const form          = event.currentTarget as HTMLFormElement;
            const password      = field(form, 'password',     false);
            const sharedSecret  = field(form, 'sharedSecret', false);

            void tellCredentials(form, field(form, 'username'), password, sharedSecret);

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
                // into its forms, to be put right, and why it was refused.
                await load();

            }

        }


        async function tellCredentials(form: HTMLFormElement, username: string, password: string, sharedSecret: string): Promise<void> {

            const error = content.querySelector<HTMLElement>('#credentials-error');

            if (error)
                error.textContent = '';

            if (password.length === 0 && sharedSecret.length === 0)
            {
                if (error)
                    error.textContent = 'Fill in either a password or a shared secret.';
                return;
            }

            try
            {

                csms = await api.csms.saveCredentials({
                    username,
                    password:      password.length     > 0 ? password     : undefined,
                    sharedSecret:  sharedSecret.length > 0 ? sharedSecret : undefined
                });

                if (cancelled)
                    return;

                draw();

                // Saved: the secret typed goes, and the name says what the
                // controller signs in as now.
                form.reset();

            }
            catch (problem)
            {
                if (error && !cancelled)
                    error.textContent = errorMessage(problem);
            }

        }


        async function forgetCredentials(): Promise<void> {

            if (!window.confirm('Forget what this local controller signs in to the CSMS with? It will not be able to reconnect.'))
                return;

            try
            {

                csms = await api.csms.removeCredentials();

                if (!cancelled)
                    draw();

            }
            catch (problem)
            {
                if (!cancelled)
                    window.alert(errorMessage(problem));
            }

        }


        /**
         * The page as the controller has it now, drawn over the page as it
         * is - what is typed into its forms kept, as a draw keeps it. Reload
         * empties them itself.
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
         * Loaded anew - Reload - is what the controller has, the forms too,
         * which a draw on its own would leave as typed.
         */
        async function reload(): Promise<void> {

            await load();

            if (!cancelled)
                content.querySelectorAll('form').forEach(form => form.reset());

        }


        // Both forms are drafts until they are saved, and a password typed but
        // not saved cannot be read back off the page to try again.
        const release = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        return () => { cancelled = true; release(); };

    }

};
