import { api, type CSMSConfiguration } from '../api/client';
import { auth } from '../auth';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, field, formatTimestamp, isChecked } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';

/**
 * The charging station management system above this local controller.
 *
 * The mirror image of the charging station page: there this controller is a
 * server that others sign in to, here it is a client that signs in to somebody
 * else. The one asymmetry worth knowing is about secrets - the passwords of the
 * stations below are hashed and cannot be read back, while what this controller
 * signs in with upwards has to stay readable, because saying it is the whole
 * point. Neither ever reaches this page.
 */
export const csmsPage: Page = {

    title: 'CSMS connection',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/csms',
            title:     'CSMS connection',
            subtitle:  'The charging station management system this local controller reports to.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        // Reload throws what is typed into a form away as thoroughly as leaving
        // the page does, and from the opposite corner of the screen, so it
        // asks first.
        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void load();
        });

        const mayChange = auth.can('changeStationSettings');

        let cancelled = false;
        let csms: CSMSConfiguration | null = null;


        function draw(): void {

            if (csms === null)
                return;

            const settings = csms;
            const waiting  = settings.state.waitingForARestart;

            render(content, html`

                ${mayChange ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at this
                        but not change it.
                    </div>
                `}

                ${waiting.length === 0 ? '' : html`
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
                </div>
            `);

            wire();

        }


        function stateCard(settings: CSMSConfiguration): HTMLFragment {

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
                        ` : ''}

                        <dt>Signs in as</dt>
                        <dd>${settings.credentials.username ?? html`<em>nothing set</em>`}</dd>

                    </dl>

                    ${settings.state.lastProblem === null ? '' : html`
                        <div class="notice warn">${settings.state.lastProblem}</div>
                    `}

                    ${settings.enabled && !settings.state.hasCredentials ? html`
                        <div class="notice warn">
                            This local controller is meant to report to a CSMS but has nothing to sign in with.
                            Set it below.
                        </div>
                    ` : ''}

                </section>
            `;

        }


        function connectionCard(settings: CSMSConfiguration): HTMLFragment {

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-plug"></i> Where it reports to</h2>

                    <form id="connection-form" class="form-stack">

                        <label class="switch">
                            <input type="checkbox" name="enabled" ${settings.enabled ? html`checked` : ''}
                                   ${mayChange ? '' : html`disabled`} />
                            <span>Report to a CSMS</span>
                        </label>

                        <label>Address
                            <input type="text" name="url" maxlength="400" value="${settings.url ?? ''}"
                                   placeholder="wss://csms.example.org/ocpp/lc001"
                                   ${mayChange ? '' : html`disabled`} />
                            <span class="hint">
                                A WebSocket address. <code>wss://</code> for security profiles 2 and 3,
                                <code>ws://</code> only for profile 1.
                            </span>
                        </label>

                        <label>Security profile
                            <select name="securityProfile" ${mayChange ? '' : html`disabled`}>
                                <option value="1" ${settings.securityProfile === 1 ? html`selected` : ''}>
                                    1 - a password, unencrypted
                                </option>
                                <option value="2" ${settings.securityProfile === 2 ? html`selected` : ''}>
                                    2 - a password over TLS
                                </option>
                                <option value="3" ${settings.securityProfile === 3 ? html`selected` : ''}>
                                    3 - a client certificate over TLS
                                </option>
                            </select>
                        </label>

                        <div class="notice">
                            <strong>Profile 1 sends the password in the clear.</strong> The line to a backend
                            crosses networks this site does not own, so profile 2 is the least that makes sense
                            outside a laboratory. Profile 3 needs a client certificate of this controller's own,
                            which there is no store for yet - it is refused with that said rather than tried.
                        </div>

                        <label>Ping every
                            <input type="number" name="pingEvery" min="5" max="3600"
                                   value="${settings.pingEvery}" ${mayChange ? '' : html`disabled`} />
                            <span class="hint">Seconds. How a CSMS that quietly went away is noticed.</span>
                        </label>

                        <label>Dial again after
                            <input type="number" name="reconnectInitialDelay" min="1" max="600"
                                   value="${settings.reconnectInitialDelay}" ${mayChange ? '' : html`disabled`} />
                            <span class="hint">
                                Seconds before the first attempt. The wait doubles after each failure, up to
                                ${settings.reconnectMaxDelay} seconds.
                            </span>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
                            <span id="connection-note" class="form-note"></span>
                            <span id="connection-error" class="form-error" role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        function credentialsCard(settings: CSMSConfiguration): HTMLFragment {

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
                            ${settings.credentials.hasPassword ? html`<span class="badge">password</span> ` : ''}
                            ${settings.credentials.hasTOTP ? html`<span class="badge">one-time token</span>` : ''}
                        </p>
                    ` : html`<p class="muted">Nothing set yet.</p>`}

                    <form id="credentials-form" class="form-stack">

                        <label>Signs in as
                            <input type="text" name="username" maxlength="48"
                                   value="${settings.credentials.username ?? ''}"
                                   placeholder="lc001" ${mayChange ? '' : html`disabled`} required />
                        </label>

                        <label>Password
                            <input type="text" name="password"
                                   minlength="${settings.credentials.minPasswordLength}" maxlength="64"
                                   placeholder="what the CSMS issued" ${mayChange ? '' : html`disabled`} />
                        </label>

                        <label>Or a TOTP shared secret
                            <input type="text" name="sharedSecret" maxlength="128"
                                   placeholder="what the CSMS issued" ${mayChange ? '' : html`disabled`} />
                            <span class="hint">
                                Fill in one of the two. A shared secret cannot be made up here either: the other
                                end has to know it.
                            </span>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
                            <button type="button" id="credentials-remove" class="btn danger"
                                    ${mayChange && (settings.credentials.hasPassword || settings.credentials.hasTOTP)
                                          ? '' : html`disabled`}>
                                Forget them
                            </button>
                            <span id="credentials-error" class="form-error" role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        function wire(): void {

            if (!mayChange)
                return;

            must<HTMLFormElement>(content, '#connection-form').addEventListener('submit', event => {

                event.preventDefault();

                const form = event.target as HTMLFormElement;

                void save({
                    enabled:                isChecked(form, 'enabled'),
                    url:                    field(form, 'url', false),
                    securityProfile:        Number(field(form, 'securityProfile')),
                    pingEvery:              Number(field(form, 'pingEvery')),
                    reconnectInitialDelay:  Number(field(form, 'reconnectInitialDelay'))
                });

            });

            must<HTMLFormElement>(content, '#credentials-form').addEventListener('submit', event => {

                event.preventDefault();

                const form          = event.target as HTMLFormElement;
                const password      = field(form, 'password',     false);
                const sharedSecret  = field(form, 'sharedSecret', false);

                void saveCredentials(field(form, 'username'), password, sharedSecret);

            });

            content.querySelector<HTMLButtonElement>('#credentials-remove')
                   ?.addEventListener('click', () => void forgetCredentials());

        }


        async function save(update: Parameters<typeof api.csms.save>[0]): Promise<void> {

            const note  = content.querySelector<HTMLElement>('#connection-note');
            const error = content.querySelector<HTMLElement>('#connection-error');

            if (note)   note.textContent  = 'Saving ...';
            if (error)  error.textContent = '';

            try
            {

                csms = await api.csms.save(update);

                if (cancelled)
                    return;

                draw();

            }
            catch (problem)
            {

                if (cancelled)
                    return;

                if (note)   note.textContent  = '';
                if (error)  error.textContent = errorMessage(problem);

                // What was refused is not what is running, so the form has to
                // go back to saying what is true.
                void load(false);

            }

        }


        async function saveCredentials(username: string, password: string, sharedSecret: string): Promise<void> {

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


        async function load(showLoading = true): Promise<void> {

            if (showLoading)
                render(content, html`<div class="loading">Loading ...</div>`);

            try
            {

                const settings = await api.csms.get();

                if (cancelled)
                    return;

                csms = settings;

                draw();

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`<div class="error-box">${errorMessage(problem)}</div>`);
            }

        }


        // Both forms are drafts until they are saved, and a password typed but
        // not saved cannot be read back off the page to try again.
        const release = unsaved.heldBy(() => Array.from(content.querySelectorAll<HTMLFormElement>('form')).
                                                   some(form => typedSinceDrawn(form)));

        void load();

        return () => { cancelled = true; release(); };

    }

};
