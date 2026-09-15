import { api, type DNSConfiguration, type DNSQueryResult, type DNSServer, type DNSUpdate } from '../api/client';
import { auth } from '../auth';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, formatValue, humanizeKey } from '../ui';

/**
 * How this local controller resolves names.
 *
 * Everything on this page takes effect the moment it is saved, for everything
 * inside the local controller that resolves anything - the name servers included, which
 * are exchanged in the client rather than in a copy of it. Nothing here waits
 * for a restart.
 *
 * Switching name resolution off takes the servers away from the client, which
 * is what off means: a query then fails at once instead of quietly going to
 * whatever the machine happens to have configured.
 */
export const dnsPage: Page = {

    title: 'DNS client',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/dns',
            title:     'DNS client',
            subtitle:  'How this local controller resolves names.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => void load());

        const mayChange  = auth.can('changeNetworkSettings');
        const mayTest    = auth.can('runDiagnostics');

        let cancelled    = false;
        let current: DNSConfiguration | null = null;

        /** The servers on screen; edited as a list and sent as one value. */
        let servers: DNSServer[] = [];

        let result: DNSQueryResult | null = null;
        let testing = false;

        /**
         * What was last typed into the test.
         *
         * Kept outside the template because every redraw builds the form
         * afresh: without this, the name being looked up would disappear from
         * the field the moment the button said "Asking ...".
         */
        let testName: string = '';
        let testTypes: string[] = ['A', 'AAAA'];


        function draw(): void {

            if (current === null)
                return;

            const configuration = current;

            render(content, html`

                ${mayChange ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at the name
                        resolution but not change it. That needs the CPO or the system administrator role.
                    </div>
                `}

                <div class="cards">

                    <section class="card">

                        <h2><i class="fa-solid fa-power-off"></i> Name resolution</h2>

                        <label class="switch">
                            <input type="checkbox" id="enabled"
                                   ${configuration.enabled ? html`checked` : ''}
                                   ${mayChange ? '' : html`disabled`} />
                            <span>${configuration.enabled ? 'switched on' : 'switched off'}</span>
                        </label>

                        <p class="hint">
                            Switched off, this local controller asks nobody: the name servers are taken away from
                            the client, so anything that tries to resolve a name fails at once and says
                            why, instead of falling back to whatever this machine has configured.
                        </p>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-server"></i> Name servers</h2>

                        <div class="server-list" id="servers">
                            ${servers.length === 0
                                  ? html`<p class="muted small">No name server configured.</p>`
                                  : servers.map((server, index) => html`
                                      <div class="server-row" data-index="${index}">
                                          <input type="text" data-field="address" data-index="${index}"
                                                 value="${server.address}" placeholder="address or host name"
                                                 ${mayChange ? '' : html`disabled`} />
                                          <input type="number" data-field="port" data-index="${index}"
                                                 value="${server.port}" min="1" max="65535" class="port"
                                                 ${mayChange ? '' : html`disabled`} />
                                          <select data-field="transport" data-index="${index}" ${mayChange ? '' : html`disabled`}>
                                              ${configuration.limits.transports.map(transport => html`
                                                  <option value="${transport}" ${transport === server.transport ? html`selected` : ''}>${transport}</option>
                                              `)}
                                          </select>
                                          <button type="button" class="btn small danger" data-remove="${index}"
                                                  ${mayChange ? '' : html`disabled`}>Remove</button>
                                      </div>
                                  `)}
                        </div>

                        <div class="form-actions">
                            <button type="button" id="add-server" class="btn"
                                    ${!mayChange || servers.length >= configuration.limits.maxServers ? html`disabled` : ''}>
                                Add a server
                            </button>
                            <span class="hint">At most ${configuration.limits.maxServers}. They are asked in parallel; the first usable answer wins.</span>
                        </div>

                    </section>

                    <section class="card">

                        <h2><i class="fa-solid fa-sliders"></i> Settings</h2>

                        <form id="dns-form" class="form-stack">

                            <label class="checkbox">
                                <input type="checkbox" name="useCache" ${configuration.settings.useCache ? html`checked` : ''} ${mayChange ? '' : html`disabled`} />
                                Use the cache
                                <span class="hint">Answer from what was already asked, for as long as its time to live says.</span>
                            </label>

                            <label class="checkbox">
                                <input type="checkbox" name="dnssecOK" ${configuration.settings.dnssecOK ? html`checked` : ''} ${mayChange ? '' : html`disabled`} />
                                DNSSEC OK
                                <span class="hint">Ask the server for the signatures, by setting the DO bit.</span>
                            </label>

                            <label class="checkbox">
                                <input type="checkbox" name="followCNAMEs" ${configuration.settings.followCNAMEs ? html`checked` : ''} ${mayChange ? '' : html`disabled`} />
                                Follow CNAMEs
                            </label>

                            <label>Recursion desired
                                <select name="recursionDesired" ${mayChange ? '' : html`disabled`}>
                                    <option value=""      ${configuration.settings.recursionDesired === null  ? html`selected` : ''}>leave it to the server</option>
                                    <option value="true"  ${configuration.settings.recursionDesired === true  ? html`selected` : ''}>yes</option>
                                    <option value="false" ${configuration.settings.recursionDesired === false ? html`selected` : ''}>no</option>
                                </select>
                            </label>

                            <label>Query timeout in seconds
                                <input type="number" name="queryTimeoutSeconds" min="0.1" max="${configuration.limits.maxQueryTimeout}"
                                       step="0.1" value="${configuration.settings.queryTimeoutSeconds}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label>Maximum CNAME follows
                                <input type="number" name="maxCNAMEFollows" min="0" max="255" value="${configuration.settings.maxCNAMEFollows}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <label>Maximum retries
                                <input type="number" name="maxRetries" min="0" max="255" value="${configuration.settings.maxRetries}" ${mayChange ? '' : html`disabled`} />
                            </label>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>Save</button>
                                <span id="form-note"  class="form-notice" role="status"></span>
                                <span id="form-error" class="form-error"  role="alert"></span>
                            </div>

                            <span class="hint">Saved to ${configuration.file}, and in effect at once.</span>

                        </form>

                    </section>

                    <section class="card wide">

                        <h2><i class="fa-solid fa-vial"></i> Test</h2>

                        <form id="query-form" class="query-form">

                            <label>Name
                                <input type="text" name="name" placeholder="example.org" required
                                       value="${testName}" ${mayTest ? '' : html`disabled`} />
                            </label>

                            <div class="record-types">
                                <span class="k">Record types</span>
                                <div class="chips" id="record-types">
                                    ${configuration.limits.recordTypes.map(recordType => html`
                                        <button type="button" class="chip tag-button ${testTypes.includes(recordType) ? 'on' : ''}"
                                                data-type="${recordType}" aria-pressed="${testTypes.includes(recordType)}"
                                                ${mayTest ? '' : html`disabled`}>${recordType}</button>
                                    `)}
                                </div>
                            </div>

                            <div class="form-actions">
                                <button type="submit" class="btn primary" ${mayTest && !testing ? '' : html`disabled`}>
                                    ${testing ? 'Asking ...' : 'Look it up'}
                                </button>
                                <span id="query-error" class="form-error" role="alert"></span>
                            </div>

                            <span class="hint">
                                ${mayTest
                                      ? html`Every step is written to the log, so the Logs page of anybody watching shows it too.`
                                      : html`Running a query needs the CPO or the system administrator role.`}
                            </span>

                        </form>

                        ${result === null ? '' : html`
                            <div class="query-result ${result.ok ? 'ok' : 'bad'}">

                                <div class="kv-list">
                                    <div class="kv"><span class="k">Name</span><span class="v">${result.name}</span></div>
                                    <div class="kv"><span class="k">Asked for</span><span class="v">${result.recordTypes.join(', ')}</span></div>
                                    ${result.error
                                          ? html`<div class="kv"><span class="k">Error</span><span class="v">${result.error}</span></div>`
                                          : html`
                                              <div class="kv"><span class="k">Response code</span><span class="v">${result.responseCode ?? '-'}</span></div>
                                              <div class="kv"><span class="k">Answered by</span><span class="v">${result.server ?? '-'}</span></div>
                                              <div class="kv"><span class="k">Took</span><span class="v">${result.runtime_ms ?? '-'} ms</span></div>
                                              ${result.dnssec ? html`<div class="kv"><span class="k">DNSSEC</span><span class="v">${result.dnssec}</span></div>` : ''}
                                          `}
                                </div>

                                ${result.answers.length === 0
                                      ? html`<p class="muted small">No records came back.</p>`
                                      : html`
                                          <div class="table-scroll">
                                              <table class="records">
                                                  <thead>
                                                      <tr><th>Name</th><th>Type</th><th>TTL</th><th>Value</th></tr>
                                                  </thead>
                                                  <tbody>
                                                      ${result.answers.map(record => html`
                                                          <tr>
                                                              <td>${record.name}</td>
                                                              <td>${record.type}</td>
                                                              <td>${record.timeToLive}s</td>
                                                              <td class="value">${record.value}</td>
                                                          </tr>
                                                      `)}
                                                  </tbody>
                                              </table>
                                          </div>
                                          ${result.more ? html`<p class="muted small">and ${result.more} more.</p>` : ''}
                                      `}

                            </div>
                        `}

                    </section>

                    <section class="card">
                        <h2><i class="fa-solid fa-circle-info"></i> Fixed when the client was made</h2>
                        <div class="kv-list">
                            ${Object.entries(configuration.fixed).map(([key, value]) => html`
                                <div class="kv">
                                    <span class="k">${humanizeKey(key)}</span>
                                    <span class="v">${formatValue(value)}</span>
                                </div>
                            `)}
                        </div>
                        <p class="hint">Changing these means making another client, which means restarting the local controller.</p>
                    </section>

                </div>

            `);

            wire();

        }


        function wire(): void {

            const list = must<HTMLElement>(content, '#servers');

            list.addEventListener('input', event => {

                const input = event.target as HTMLInputElement;
                const index = Number(input.dataset.index);
                const field = input.dataset.field;

                if (!field || Number.isNaN(index))
                    return;

                if (field === 'address')
                    servers[index].address = input.value;
                else if (field === 'port')
                    servers[index].port = Number(input.value);

            });

            list.addEventListener('change', event => {

                const select = event.target as HTMLSelectElement;

                if (select.dataset.field === 'transport')
                    servers[Number(select.dataset.index)].transport = select.value;

            });

            list.addEventListener('click', event => {

                const remove = (event.target as HTMLElement).closest<HTMLElement>('[data-remove]');

                if (remove) {
                    servers.splice(Number(remove.dataset.remove), 1);
                    draw();
                }

            });

            must<HTMLButtonElement>(content, '#add-server').addEventListener('click', () => {
                servers.push({ address: '', port: 53, transport: 'UDP', queryTimeoutSeconds: null });
                draw();
            });

            must<HTMLInputElement>(content, '#enabled').addEventListener('change', event => {
                void save({ enabled: (event.target as HTMLInputElement).checked });
            });

            must<HTMLFormElement>(content, '#dns-form').addEventListener('submit', event => {

                event.preventDefault();

                const form = event.target as HTMLFormElement;
                const data = new FormData(form);
                const tri  = String(data.get('recursionDesired') ?? '');

                void save({
                    // The servers travel with the settings, because the form is
                    // where somebody presses Save after editing either.
                    servers:              servers.filter(server => server.address.trim().length > 0),
                    useCache:             data.get('useCache')     !== null,
                    dnssecOK:             data.get('dnssecOK')     !== null,
                    followCNAMEs:         data.get('followCNAMEs') !== null,
                    recursionDesired:     tri === '' ? null : tri === 'true',
                    queryTimeoutSeconds:  Number(data.get('queryTimeoutSeconds')),
                    maxCNAMEFollows:      Number(data.get('maxCNAMEFollows')),
                    maxRetries:           Number(data.get('maxRetries'))
                });

            });

            const types = must<HTMLElement>(content, '#record-types');

            types.addEventListener('click', event => {

                const chip = (event.target as HTMLElement).closest<HTMLElement>('[data-type]');

                if (chip) {

                    const on   = chip.classList.toggle('on');
                    const type = chip.dataset.type!;

                    chip.setAttribute('aria-pressed', String(on));

                    testTypes = on
                                    ? [...testTypes, type]
                                    : testTypes.filter(other => other !== type);

                }

            });

            // Remembered as it is typed, so that the redraw below keeps it.
            must<HTMLInputElement>(content, '#query-form input[name="name"]').
                addEventListener('input', event => {
                    testName = (event.target as HTMLInputElement).value;
                });

            must<HTMLFormElement>(content, '#query-form').addEventListener('submit', event => {
                event.preventDefault();
                void runQuery(event.target as HTMLFormElement);
            });

        }


        async function save(update: DNSUpdate): Promise<void> {

            const note  = must<HTMLElement>(content, '#form-note');
            const error = must<HTMLElement>(content, '#form-error');

            note.textContent  = '';
            error.textContent = '';

            try
            {
                // The answer is the whole configuration as it now stands, so
                // the page shows what the local controller took rather than what the
                // form sent.
                current = await api.dns.save(update);
                servers = current.servers.map(server => ({ ...server }));

                draw();

                must<HTMLElement>(content, '#form-note').textContent = 'Saved, and in effect.';
            }
            catch (problem)
            {
                error.textContent = errorMessage(problem);
            }

        }


        async function runQuery(form: HTMLFormElement): Promise<void> {

            const error = must<HTMLElement>(content, '#query-error');

            testName          = String(new FormData(form).get('name') ?? '').trim();
            error.textContent = '';

            if (testName.length === 0) {
                error.textContent = 'A name is needed.';
                return;
            }

            testing = true;
            draw();

            try
            {
                result = await api.dns.query(testName, testTypes);
            }
            catch (problem)
            {
                result = null;
                testing = false;
                draw();
                must<HTMLElement>(content, '#query-error').textContent = errorMessage(problem);
                return;
            }

            testing = false;
            draw();

        }


        async function load(): Promise<void> {

            try
            {
                const loaded = await api.dns.get();

                if (cancelled)
                    return;

                current = loaded;
                servers = loaded.servers.map(server => ({ ...server }));

                draw();
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The DNS configuration could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        void load();

        return () => { cancelled = true; };

    }

};
