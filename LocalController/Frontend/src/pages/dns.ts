import { api, type DNSConfiguration, type DNSQueryResult, type DNSServer, type DNSUpdate } from '../api/client';
import { auth } from '../auth';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, formatValue, humanizeKey, whileSaving } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';

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

        // Reload throws a draft away just as thoroughly as "Discard changes"
        // does, and from the opposite corner of the screen, so it asks first.
        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void load();
        });

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

                <div class="cards stacked">

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
                                          <button type="button" class="btn small" data-ask="${index}"
                                                  title="Ask this server, and only this one"
                                                  ${mayTest ? '' : html`disabled`}>Test</button>
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

                    <section class="card">

                        <h2><i class="fa-solid fa-vial"></i> Test</h2>

                        <p class="hint">
                            Ask for a name - or an address, which is turned around and asked as a reverse
                            lookup. Every step is written to the log, so the Logs page of anybody watching
                            shows it too.
                        </p>

                        <div class="form-actions">
                            <button type="button" class="btn primary" id="look-up" ${mayTest ? '' : html`disabled`}>
                                Look something up
                            </button>
                        </div>

                        <p class="hint">
                            ${mayTest
                                  ? html`
                                        This asks the way the local controller asks for anything: the servers above, in
                                        turn, until one answers. To find out what one particular server says,
                                        use the button on its own row.
                                    `
                                  : html`Running a query needs the CPO or the system administrator role.`}
                        </p>

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

            must<HTMLButtonElement>(content, '#look-up').addEventListener('click', () => lookUp(null));

            list.addEventListener('click', event => {

                const asking = (event.target as HTMLElement).closest<HTMLElement>('[data-ask]');

                if (asking && !(asking as HTMLButtonElement).disabled)
                    lookUp(Number(asking.dataset.ask));

            });

        }


        async function save(update: DNSUpdate): Promise<void> {

            const note = must<HTMLElement>(content, '#form-note');

            note.textContent = '';

            must<HTMLElement>(content, '#form-error').textContent = '';

            try
            {
                // The answer is the whole configuration as it now stands, so
                // the page shows what the local controller took rather than what the
                // form sent.
                current = await whileSaving(content, note, () => api.dns.save(update));
                servers = current.servers.map(server => ({ ...server }));

                draw();

                must<HTMLElement>(content, '#form-note').textContent = 'Saved, and in effect.';
            }
            catch (problem)
            {
                must<HTMLElement>(content, '#form-error').textContent = errorMessage(problem);
            }

        }


        /**
         * What each name server is allowed, in seconds: its own timeout where
         * it has one, and the client's where it has not.
         *
         * The local controller tries them in turn, so this is the list the page has to
         * be willing to wait for - what it is given up on is the local controller going
         * quiet, never the patience the local controller was configured with.
         */
        function whatTheServersAreAllowed(): number[] {
            return (current?.servers ?? []).map((_, index) => whatOneServerIsAllowed(index));
        }

        /**
         * The longest one server can honestly take, in seconds.
         *
         * Its timeout times the number of attempts, and the attempts are the
         * part that is easy to forget: measured against a name server that
         * does not answer at all, a query with a ten second timeout came back
         * after twenty, because the local controller tries again. A deadline of ten
         * would have given up on a local controller that was still doing what it was
         * told.
         */
        function whatOneServerIsAllowed(index: number): number {

            const timeout = current?.servers[index]?.queryTimeoutSeconds ??
                            current?.settings.queryTimeoutSeconds ?? 0;

            return timeout * ((current?.settings.maxRetries ?? 0) + 1);

        }


        /**
         * What came back, as it was shown on the page before this moved into a
         * dialog.
         *
         * A function rather than a block inside the dialog, because it is the
         * one part of this that is worth reading on its own: it is what
         * somebody actually looks at, and the dialog around it is plumbing.
         */
        function resultView(result: DNSQueryResult): HTMLFragment {

            return html`
                <div class="query-result ${result.ok ? 'ok' : 'bad'}">

                    ${result.turnedAround ? html`<div class="notice">${result.turnedAround}</div>` : ''}

                    <div class="kv-list">
                        <div class="kv"><span class="k">Asked for</span><span class="v">${result.name}</span></div>
                        <div class="kv"><span class="k">Record types</span><span class="v">${result.recordTypes.join(', ')}</span></div>
                        ${result.asked
                              ? html`<div class="kv"><span class="k">Asked of</span><span class="v">${result.asked}</span></div>`
                              : ''}
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
            `;

        }

        /**
         * The lookup dialog, for all the servers at once or for one of them.
         *
         * A dialog rather than a card on the page, because a lookup is a
         * question somebody asks several times in a row with small changes -
         * another type, another name, the next server - and each answer is a
         * table. On the page that pushed the settings out of sight every time;
         * here the settings stay where they were and the answers replace each
         * other in one place.
         *
         * @param server  the place in the list of the one server to ask, or
         *                null to ask the way the local controller asks for anything.
         */
        function lookUp(server: number | null): void {

            if (current === null)
                return;

            const configuration = current;
            const asking        = server === null ? null : servers[server];

            if (server !== null && asking === undefined)
                return;

            const dialog = document.createElement('dialog');

            dialog.className = 'test-dialog';

            document.body.appendChild(dialog);

            /** Shut it and take it away - both, as not every browser fires "close". */
            const dismiss = (): void => { dialog.close(); dialog.remove(); };

            let asked: DNSQueryResult | null = null;
            let busy = false;

            function paint(): void {

                render(dialog, html`

                    <h2><i class="fa-solid fa-vial"></i> Look something up</h2>

                    <p class="hint">
                        ${asking === null
                              ? html`
                                    Asked the way this local controller asks for anything: the configured servers, in
                                    turn, until one answers.
                                `
                              : html`
                                    Asked of <code>${asking.address.includes(':')
                                                          ? `[${asking.address}]:${asking.port}`
                                                          : `${asking.address}:${asking.port}`}</code> over
                                    ${asking.transport} and of nothing else, and without the cache - an
                                    answer somebody else already fetched says nothing about this server.
                                    One that does not answer at all takes
                                    ${whatOneServerIsAllowed(server!)} seconds to say so.
                                `}
                    </p>

                    <form id="query-form" class="form-stack">

                        <label>Name or address
                            <input type="text" name="name" placeholder="example.org" value="${testName}"
                                   ${busy ? html`disabled` : ''} />
                            <span class="hint">
                                An address is turned around and asked as a reverse lookup, which is what
                                somebody typing one into this box means.
                            </span>
                        </label>

                        <div class="record-types">
                            <span class="k">Record types</span>
                            <div class="chips" id="record-types">
                                ${configuration.limits.recordTypes.map((recordType: string) => html`
                                    <button type="button" class="chip tag-button ${testTypes.includes(recordType) ? 'on' : ''}"
                                            data-type="${recordType}" aria-pressed="${testTypes.includes(recordType)}"
                                            ${busy ? html`disabled` : ''}>${recordType}</button>
                                `)}
                            </div>
                        </div>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ${busy ? html`disabled` : ''}>
                                ${busy ? 'Asking ...' : 'Ask'}
                            </button>
                            <button type="button" class="btn" id="query-close">Close</button>
                            <span id="query-error" class="form-error" role="alert"></span>
                        </div>

                    </form>

                    ${asked === null ? '' : resultView(asked)}

                `);

                must<HTMLElement>(dialog, '#record-types').addEventListener('click', event => {

                    const chip = (event.target as HTMLElement).closest<HTMLElement>('[data-type]');

                    if (chip && !(chip as HTMLButtonElement).disabled) {

                        const on   = chip.classList.toggle('on');
                        const type = chip.dataset.type!;

                        chip.setAttribute('aria-pressed', String(on));

                        testTypes = on
                                        ? [...testTypes, type]
                                        : testTypes.filter(other => other !== type);

                    }

                });

                must<HTMLInputElement>(dialog, 'input[name="name"]').
                    addEventListener('input', event => {
                        testName = (event.target as HTMLInputElement).value;
                    });

                must<HTMLButtonElement>(dialog, '#query-close').addEventListener('click', dismiss);

                must<HTMLFormElement>(dialog, '#query-form').addEventListener('submit', event => {
                    event.preventDefault();
                    void ask();
                });

            }

            async function ask(): Promise<void> {

                testName = must<HTMLInputElement>(dialog, 'input[name="name"]').value.trim();

                if (testName.length === 0) {
                    must<HTMLElement>(dialog, '#query-error').textContent = 'A name or an address is needed.';
                    return;
                }

                // The last answer goes the moment the next question is asked.
                // Left standing under "Asking ...", it read as the answer to
                // the new one - and when that one never came back, it went on
                // reading that way.
                asked = null;
                busy  = true;
                paint();

                try
                {
                    // One server means one server's patience, not every
                    // server's added up: waiting two minutes for a question
                    // that was only ever put to one of them is a page that
                    // looks broken.
                    asked = await api.dns.query(testName,
                                                testTypes,
                                                server === null
                                                    ? whatTheServersAreAllowed()
                                                    : [ whatOneServerIsAllowed(server) ],
                                                server ?? undefined);
                }
                catch (problem)
                {
                    asked = null;
                    busy  = false;
                    paint();
                    must<HTMLElement>(dialog, '#query-error').textContent = errorMessage(problem);
                    return;
                }

                busy = false;
                paint();

            }

            dialog.addEventListener('close',  dismiss);
            dialog.addEventListener('cancel', dismiss);

            paint();
            dialog.showModal();

            must<HTMLInputElement>(dialog, 'input[name="name"]').focus();

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

        // The settings are a form and answer for themselves; the name servers
        // are a list, which is redrawn as it is edited and therefore always
        // looks untouched - so it is compared with what the local controller last said.
        const release = unsaved.heldBy(
                            () => typedSinceDrawn(content.querySelector('#dns-form')) ||
                                  JSON.stringify(servers) !== JSON.stringify(current?.servers ?? [])
                        );

        void load();

        return () => { cancelled = true; release(); };

    }

};
