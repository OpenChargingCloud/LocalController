import { api, type OCPPServerConfiguration, type OCPPServerUpdate, type StationLogins } from '../api/client';
import { auth } from '../auth';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, field, formatTimestamp, isChecked } from '../ui';

/**
 * The server the charging stations connect to, and which of them may.
 *
 * Two things on one page because they are one question: a port nobody can come
 * through is the same as no port. The certificates it presents and the chains
 * it accepts are their own pages - those are about who this controller is and
 * whom it believes, and they are a different permission.
 *
 * What is decided when the socket opens cannot be changed under a running
 * server, so the page says which of the fields it just saved are waiting for
 * the next start rather than letting somebody find out from a charging station.
 */
export const ocppServerPage: Page = {

    title: 'Charging stations',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/ocpp-server',
            title:     'Charging stations',
            subtitle:  'The HTTP WebSocket server the charging stations below this local controller connect to.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => void load());

        const mayChange = auth.can('changeStationSettings');

        let cancelled = false;
        let server:   OCPPServerConfiguration | null = null;
        let stations: StationLogins           | null = null;

        function draw(): void {

            if (server === null || stations === null)
                return;

            const configuration = server;
            const logins        = stations;
            const waiting       = configuration.state.waitingForARestart;

            render(content, html`

                ${mayChange ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at the charging
                        station server but not change it. That needs the CPO or the system administrator role.
                    </div>
                `}

                ${waiting.length === 0 ? '' : html`
                    <div class="notice warn">
                        <strong>Saved, and waiting for the next start:</strong> ${waiting.join(', ')}.
                        The socket is opened once, when the server is built - so these are in the configuration
                        file but not in effect. Everything else on this page took effect at once.
                    </div>
                `}

                <div class="cards">

                    ${stateCard(configuration)}

                    <section class="card">

                        <h2><i class="fa-solid fa-power-off"></i> The server</h2>

                        <label class="switch">
                            <input type="checkbox" id="enabled"
                                   ${configuration.enabled ? html`checked` : ''}
                                   ${mayChange ? '' : html`disabled`} />
                            <span>${configuration.enabled ? 'switched on' : 'switched off'}</span>
                        </label>

                        <p class="hint">
                            Switched off, no charging station can connect at all. This is the one setting of this
                            local controller that opens a port to a network rather than reaching out to one, so it
                            starts switched off and stays that way until somebody says otherwise.
                        </p>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-network-wired"></i> Socket</h2>

                        <form id="socket-form" class="form-stack">

                            <label>Listen on
                                <input type="text" name="address" value="${configuration.address ?? ''}"
                                       placeholder="0.0.0.0" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label>TCP port
                                <input type="number" name="port" min="1" max="65535"
                                       value="${configuration.port}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <fieldset class="checks">
                                <legend>OCPP versions offered</legend>
                                ${configuration.limits.subprotocols.map(subprotocol => html`
                                    <label class="check">
                                        <input type="checkbox" name="subprotocol" value="${subprotocol}"
                                               ${configuration.subprotocols.includes(subprotocol) ? html`checked` : ''}
                                               ${mayChange ? '' : html`disabled`} />
                                        <span>${subprotocol}</span>
                                    </label>
                                `)}
                            </fieldset>

                            <label>Oldest TLS version accepted
                                <select name="minTLSVersion" ${mayChange ? '' : html`disabled`}>
                                    ${configuration.limits.tlsVersions.map(version => html`
                                        <option value="${version}"
                                                ${version === configuration.minTLSVersion ? html`selected` : ''}>
                                            TLS ${version}
                                        </option>
                                    `)}
                                </select>
                            </label>

                            <label>At most this many connected at once
                                <input type="number" name="maxConnections" min="1" max="${configuration.limits.maxConnections}"
                                       value="${configuration.maxConnections}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label>Ping a silent connection every ... seconds
                                <input type="number" name="pingEverySeconds" min="5" max="3600"
                                       value="${configuration.pingEverySeconds}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <span class="hint">
                                This is how a charging station that went away without closing its connection is
                                noticed, so it has to be shorter than the idle timeout of whatever is in between -
                                over mobile networks and behind NAT, frequently a minute or two.
                            </span>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
                                <span id="socket-note"  class="form-notice" role="status"></span>
                                <span id="socket-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">
                                Everything here is decided when the socket opens, so saving it writes the file and
                                the next start puts it into effect. Every charging station reconnects then.
                            </span>

                        </form>

                    </section>

                    ${profilesCard(configuration)}
                    ${reachableCard(configuration)}
                    ${loggingCard(configuration)}
                    ${loginsCard(logins)}

                </div>
            `);

            wire();

        }


        // Which OCPP security profiles a station may connect with.
        function profilesCard(configuration: OCPPServerConfiguration): HTMLFragment {

            const descriptions: Record<number, string> = {
                1: 'A password over an unencrypted connection. Only usable on a network nobody else is on.',
                2: 'A password over TLS. Needs a server certificate; without one this port does not encrypt at all.',
                3: 'A certificate on both sides, and no password. Needs the station to hold a certificate from an authority named under Accepted chains.'
            };

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-shield-halved"></i> Security profiles</h2>

                    <form id="profiles-form" class="form-stack">

                        <fieldset class="checks">
                            <legend>A charging station may connect with</legend>
                            ${configuration.limits.securityProfiles.map(profile => html`
                                <label class="check">
                                    <input type="checkbox" name="profile" value="${profile}"
                                           ${configuration.securityProfiles.includes(profile) ? html`checked` : ''}
                                           ${mayChange ? '' : html`disabled`} />
                                    <span><strong>Profile ${profile}</strong> &mdash; ${descriptions[profile] ?? ''}</span>
                                </label>
                            `)}
                        </fieldset>

                        <label class="check">
                            <input type="checkbox" name="checkCertificateRevocation"
                                   ${configuration.checkCertificateRevocation ? html`checked` : ''}
                                   ${mayChange ? '' : html`disabled`} />
                            <span>Check a station's certificate against its issuer's revocation list</span>
                        </label>

                        <span class="hint">
                            Off by default: a controller in a car park frequently has no route to the list, and a
                            check that cannot be made turns every station away on the day the network changes.
                        </span>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
                            <span id="profiles-note"  class="form-notice" role="status"></span>
                            <span id="profiles-error" class="form-error"  role="alert"></span>
                        </div>

                        ${configuration.securityProfiles.some(profile => profile > 1) && !configuration.state.hasCertificate
                              ? html`
                                  <div class="notice warn">
                                      Profile 2 or 3 is allowed, but there is no server certificate - so this port is
                                      <strong>not encrypted</strong> and only profile 1 can be used on it.
                                      <a href="/configuration/ocpp-server/certificates">Make a signing request</a>.
                                  </div>
                              `
                              : ''}

                        ${configuration.securityProfiles.includes(3) && configuration.state.trustedChains === 0
                              ? html`
                                  <div class="notice warn">
                                      Profile 3 is allowed, but no certificate authority has been named to accept
                                      charging stations from, so none can connect with a certificate. An empty list
                                      is "nobody", not "everybody" &mdash;
                                      <a href="/configuration/ocpp-server/trust">name one</a>.
                                  </div>
                              `
                              : ''}

                    </form>

                </section>
            `;

        }


        // The names a certificate has to carry.
        function reachableCard(configuration: OCPPServerConfiguration): HTMLFragment {

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-location-dot"></i> Reachable as</h2>

                    <form id="reachable-form" class="form-stack">

                        <label>One name or address per line
                            <textarea name="reachableAs" rows="4"
                                      placeholder="lc001.example.org&#10;192.168.1.10"
                                      ${mayChange ? '' : html`disabled`}>${configuration.reachableAs.join('\n')}</textarea>
                        </label>

                        <span class="hint">
                            What the charging stations dial. A certificate that does not carry these names is a
                            certificate they refuse, whatever else is right about it - so this list is what goes
                            into a signing request, and what an uploaded certificate is held against.
                        </span>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
                            <span id="reachable-note"  class="form-notice" role="status"></span>
                            <span id="reachable-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        // What of this server ends up in the log.
        function loggingCard(configuration: OCPPServerConfiguration): HTMLFragment {

            const logging = configuration.logging;
            const window  = Math.round(configuration.limits.suggestedPayloadWindowSeconds / 60);

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-list-check"></i> What goes into the log</h2>

                    <form id="logging-form" class="form-stack">

                        <label class="check">
                            <input type="checkbox" name="connections" ${logging.connections ? html`checked` : ''}
                                   ${mayChange ? '' : html`disabled`} />
                            <span>Charging stations connecting and disconnecting</span>
                        </label>

                        <label class="check">
                            <input type="checkbox" name="authentication" ${logging.authentication ? html`checked` : ''}
                                   ${mayChange ? '' : html`disabled`} />
                            <span>Refused sign-ins and failed TLS handshakes</span>
                        </label>

                        <span class="hint">
                            The switch to leave alone: it is the only record of somebody trying.
                        </span>

                        <label class="check">
                            <input type="checkbox" name="messages" ${logging.messages ? html`checked` : ''}
                                   ${mayChange ? '' : html`disabled`} />
                            <span>Every OCPP message, by name and identification</span>
                        </label>

                        <label class="check">
                            <input type="checkbox" name="pings" ${logging.pings ? html`checked` : ''}
                                   ${mayChange ? '' : html`disabled`} />
                            <span>The WebSocket pings that keep a connection open</span>
                        </label>

                        <hr />

                        <label class="check">
                            <input type="checkbox" name="payloads" ${logging.payloads ? html`checked` : ''}
                                   ${mayChange ? '' : html`disabled`} />
                            <span>...and what those messages carry, for the next ${window} minutes</span>
                        </label>

                        <div class="notice">
                            An OCPP message carries identification tokens, RFID card numbers and meter readings:
                            who charged, where and how much. This is a decision about personal data and not a
                            verbosity setting, so it cannot be switched on without saying when it goes off again -
                            and it goes off by itself at that moment, whether or not anybody remembered.
                            ${logging.payloadsUntil !== null
                                  ? html`<br /><strong>${logging.payloadsNow ? 'On until' : 'The window ended at'}
                                         ${formatTimestamp(logging.payloadsUntil)}.</strong>`
                                  : ''}
                        </div>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
                            <span id="logging-note"  class="form-notice" role="status"></span>
                            <span id="logging-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        // Who may sign in now lives on its own page: it grew groups, one-time
        // tokens and two kinds of credential per login, none of which belongs
        // beside the socket settings.
        function loginsCard(logins: StationLogins): HTMLFragment {

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-users-gear"></i> Who may sign in</h2>

                    <p>
                        <strong>${logins.enabled}</strong> of ${logins.stations.length} charging station(s) could
                        sign in right now, in ${logins.groups.length} group(s).
                    </p>

                    <p class="hint">
                        Which stations those are, what each of them signs in with, and what their group allows
                        them is managed on its own page.
                    </p>

                    <div class="form-actions">
                        <a class="btn" href="/configuration/ocpp-server/logins">Logins and groups</a>
                    </div>

                </section>
            `;

        }


        function stateCard(configuration: OCPPServerConfiguration): HTMLFragment {

            return html`
                <section class="card">

                    <h2><i class="fa-solid fa-circle-info"></i> Right now</h2>

                    <dl class="kv">
                        <dt>Listening</dt>
                        <dd class="${configuration.state.running ? 'ok' : 'muted'}">
                            ${configuration.state.running ? 'yes' : 'no'}
                        </dd>

                        <dt>Encrypted</dt>
                        <dd class="${configuration.state.tls ? 'ok' : 'warn'}">
                            ${configuration.state.tls ? 'TLS' : 'no - security profile 1 only'}
                        </dd>

                        <dt>Stations connect to</dt>
                        <dd><code>${configuration.state.url}</code></dd>

                        <dt>Connected</dt>
                        <dd>${configuration.state.connections}</dd>

                        <dt>Stations that may sign in</dt>
                        <dd>${configuration.state.stationLogins}</dd>

                        <dt>Accepted chains</dt>
                        <dd>${configuration.state.trustedChains}</dd>
                    </dl>

                </section>
            `;

        }


        function wire(): void {

            if (!mayChange)
                return;

            must<HTMLInputElement>(content, '#enabled').addEventListener('change', event => {
                void save({ enabled: (event.target as HTMLInputElement).checked }, 'socket');
            });

            must<HTMLFormElement>(content, '#socket-form').addEventListener('submit', event => {

                event.preventDefault();

                const form = event.target as HTMLFormElement;

                void save({
                    address:           field(form, 'address') || undefined,
                    port:              Number(field(form, 'port')),
                    subprotocols:      checked(form, 'subprotocol'),
                    minTLSVersion:     field(form, 'minTLSVersion'),
                    maxConnections:    Number(field(form, 'maxConnections')),
                    pingEverySeconds:  Number(field(form, 'pingEverySeconds'))
                }, 'socket');

            });

            must<HTMLFormElement>(content, '#profiles-form').addEventListener('submit', event => {

                event.preventDefault();

                const form = event.target as HTMLFormElement;

                void save({
                    securityProfiles:            checked(form, 'profile').map(Number),
                    checkCertificateRevocation:  isChecked(form, 'checkCertificateRevocation')
                }, 'profiles');

            });

            must<HTMLFormElement>(content, '#reachable-form').addEventListener('submit', event => {

                event.preventDefault();

                const lines = field(event.target as HTMLFormElement, 'reachableAs', false).
                                  split('\n').
                                  map(line => line.trim()).
                                  filter(line => line.length > 0);

                void save({ reachableAs: lines }, 'reachable');

            });

            must<HTMLFormElement>(content, '#logging-form').addEventListener('submit', event => {

                event.preventDefault();

                const form     = event.target as HTMLFormElement;
                const payloads = isChecked(form, 'payloads');

                // The window is computed here rather than typed: what somebody
                // is agreeing to is "for the next hour", and a date field would
                // invite "until 2099" without anybody meaning it.
                const until = payloads
                                  ? new Date(Date.now() + (server?.limits.suggestedPayloadWindowSeconds ?? 3600) * 1000).toISOString()
                                  : null;

                void save({
                    logging: {
                        connections:     isChecked(form, 'connections'),
                        authentication:  isChecked(form, 'authentication'),
                        messages:        isChecked(form, 'messages'),
                        pings:           isChecked(form, 'pings'),
                        payloads,
                        payloadsUntil:   until
                    }
                }, 'logging');

            });

        }


        async function save(update: OCPPServerUpdate, where: string): Promise<void> {

            const note  = content.querySelector<HTMLElement>(`#${where}-note`);
            const error = content.querySelector<HTMLElement>(`#${where}-error`);

            if (note)   note.textContent  = 'Saving ...';
            if (error)  error.textContent = '';

            try
            {

                server = await api.ocppServer.save(update);

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

                // What the controller refused is not what it is running, so the
                // form has to go back to saying what is true.
                void load(false);

            }

        }


        async function load(showLoading = true): Promise<void> {

            if (showLoading)
                render(content, html`<div class="loading">Loading ...</div>`);

            try
            {

                const [configuration, logins] = await Promise.all([
                    api.ocppServer.get(),
                    api.ocppServer.stations.get()
                ]);

                if (cancelled)
                    return;

                server   = configuration;
                stations = logins;

                draw();

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`<div class="error-box">${errorMessage(problem)}</div>`);
            }

        }


        void load();

        return () => { cancelled = true; };

    }

};


/** The values of every checked box of one name. */
function checked(form: HTMLFormElement, name: string): string[] {
    return new FormData(form).getAll(name).map(String);
}
