import { api, type DNSConfiguration, type DNSQueryResult, type DNSServer, type DNSUpdate } from '../api/client';
import { auth } from '../auth';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, formatValue, humanizeKey, whileSaving } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';
import { allServersTake, entryOf, isEncrypted, oneServerTakes } from './dnsServers';
import { keysOf, pinsIn, saysAnything, withPins } from './pins';
import { certificateVerdictView, heldToView, judgementView, pinsFieldset, readPinsFieldset, shownView, wirePinsFieldset } from './pinViews';

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
 *
 * A name server asked over TLS or HTTPS shows a certificate, and its row says
 * what the local controller made of it and what it is held to; a dialog changes the
 * latter in the list on screen, which is saved with everything else.
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

        const mayChange  = auth.can('dns', 'edit');
        const mayTest    = auth.can('dns', 'run');

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

                    <section class="card" id="name-servers"></section>

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
                                        This asks the way the local controller asks for anything: all of the servers
                                        above at once, and the first usable answer wins. To find out what one
                                        particular server says, use the button on its own row.
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

            drawServers();
            wire();

        }


        /**
         * The name servers, one row each - and under a row over TLS or HTTPS,
         * its certificate.
         *
         * Drawn apart from the rest of the page, because adding, removing and
         * pinning a server redraws the list, and redrawing the whole page threw
         * away whatever had just been typed into the settings below it.
         */
        function drawServers(): void {

            const configuration = current!;

            render(must<HTMLElement>(content, '#name-servers'), html`

                <h2><i class="fa-solid fa-server"></i> Name servers</h2>

                <div class="server-list" id="servers">
                    ${servers.length === 0
                          ? html`<p class="muted small">No name server configured.</p>`
                          : servers.map((server, index) => serverView(server, index))}
                </div>

                <div class="form-actions">
                    <button type="button" id="add-server" class="btn"
                            ${!mayChange || servers.length >= configuration.limits.maxServers ? html`disabled` : ''}>
                        Add a server
                    </button>
                    <span class="hint">At most ${configuration.limits.maxServers}. They are asked in parallel; the first usable answer wins.</span>
                </div>

            `);

        }


        /** One name server: its address, port and transport, and what can be done with it. */
        function serverView(server: DNSServer, index: number): HTMLFragment {

            const configuration  = current!;
            const encrypted      = isEncrypted(server.transport);

            return html`
                <div class="server-entry">

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
                        ${encrypted
                              ? html`<button type="button" class="btn small" data-pins="${index}"
                                             title="What its certificate is held to" aria-label="What its certificate is held to"
                                             ${mayChange ? '' : html`disabled`}>
                                         <i class="fa-solid fa-certificate"></i>
                                     </button>`
                              : ''}
                        <button type="button" class="btn small" data-ask="${index}"
                                title="Ask this server, and only this one"
                                ${mayTest ? '' : html`disabled`}>Test</button>
                        <button type="button" class="btn small danger" data-remove="${index}"
                                ${mayChange ? '' : html`disabled`}>Remove</button>
                    </div>

                    ${encrypted
                          ? certificateView(server)
                          : saysAnything(pinsIn(server))
                                ? html`<div class="server-certificate small">
                                           <span class="chip warn">
                                               held to a certificate, which ${server.transport} does not show: saved, it lets go of that
                                           </span>
                                       </div>`
                                : ''}

                </div>
            `;

        }


        /**
         * What the local controller made of a server's certificate, and what the list on
         * screen holds it to.
         *
         * The local controller's word only for the server as the local controller has it: a row
         * whose address, port or transport was changed and not saved is
         * another server, whose certificate nobody has looked at yet.
         */
        function certificateView(server: DNSServer): HTMLFragment {

            const saved    = asTheControllerHasIt(server);
            const pins     = pinsIn(server);
            const changed  = JSON.stringify(keysOf(pins)) !== JSON.stringify(keysOf(pinsIn(saved ?? {})));

            return html`
                <div class="server-certificate small">
                    ${saved === undefined
                          ? html`<span class="muted">not saved yet</span>`
                          : html`${certificateVerdictView(saved.judgement, saved.known)}
                                 ${shownView(saved.judgement, saved.known)}`}
                    ${heldToView(pins, changed)}
                </div>
            `;

        }


        /** The server as the local controller has it, where it has one at that address, port and transport. */
        function asTheControllerHasIt(server: DNSServer): DNSServer | undefined {

            return current?.servers.find(other => other.address   === server.address &&
                                                  other.port      === server.port    &&
                                                  other.transport === server.transport);

        }


        /**
         * Say what one server's certificate is held to, in a dialog - into the
         * list on screen, which "Save" below sends with everything else: the
         * list is one value to the local controller, and this is one more change to it.
         */
        function editPins(index: number): void {

            const server = servers[index];

            if (server === undefined)
                return;

            const saved   = asTheControllerHasIt(server);
            const name    = server.address.includes(':') ? `[${server.address}]:${server.port}` : `${server.address}:${server.port}`;
            const dialog  = document.createElement('dialog');

            dialog.className = 'test-dialog server-dialog';

            document.body.appendChild(dialog);

            /** Shut it and take it away - both, as not every browser fires "close". */
            const dismiss = (): void => { dialog.close(); dialog.remove(); };

            render(dialog, html`

                <h2><i class="fa-solid fa-certificate"></i> ${name} over ${server.transport}</h2>

                <form id="pins-form" class="form-stack">

                    ${pinsFieldset(pinsIn(server), {
                          service:  'dns',
                          shown:    saved === undefined
                                        ? null
                                        : {
                                              name,
                                              certificate:  saved.judgement?.certificate ?? saved.known?.certificate ?? null,
                                              root:         saved.judgement?.root        ?? saved.known?.root        ?? null
                                          }
                      })}

                    <div class="form-actions">
                        <button type="submit" class="btn primary">Take it into the list</button>
                        <button type="button" class="btn" id="pins-cancel">Cancel</button>
                        <span id="pins-error" class="form-error" role="alert"></span>
                    </div>

                    <span class="hint">The local controller is told when the list is saved, with "Save" under the settings.</span>

                </form>

            `);

            const form = must<HTMLFormElement>(dialog, '#pins-form');

            form.addEventListener('submit', event => {

                event.preventDefault();

                const pins = readPinsFieldset(form);

                if (pins.error !== undefined) {
                    must<HTMLElement>(dialog, '#pins-error').textContent = pins.error;
                    return;
                }

                servers[index] = withPins(servers[index], pins.draft);

                dismiss();
                drawServers();

            });

            must<HTMLButtonElement>(dialog, '#pins-cancel').addEventListener('click', dismiss);

            wirePinsFieldset(dialog);

            dialog.addEventListener('close',  dismiss);
            dialog.addEventListener('cancel', dismiss);

            dialog.showModal();

        }


        function wire(): void {

            // On the card and not on what is in it, because the list is redrawn
            // on its own and a listener on a row that was redrawn away would be
            // listening to nothing.
            const card = must<HTMLElement>(content, '#name-servers');

            card.addEventListener('input', event => {

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

            card.addEventListener('change', event => {

                const select = event.target as HTMLSelectElement;

                // Redrawn, because whether a row has a certificate to show
                // depends on it.
                if (select.dataset.field === 'transport') {
                    servers[Number(select.dataset.index)].transport = select.value;
                    drawServers();
                }

            });

            card.addEventListener('click', event => {

                const target  = event.target as HTMLElement;
                const button  = target.closest<HTMLButtonElement>('button');

                if (button === null || button.disabled)
                    return;

                if (button.id === 'add-server') {
                    servers.push({ address: '', port: 53, transport: 'UDP', queryTimeoutSeconds: null });
                    drawServers();
                }

                else if (button.dataset.remove !== undefined) {
                    servers.splice(Number(button.dataset.remove), 1);
                    drawServers();
                }

                else if (button.dataset.pins !== undefined)
                    editPins(Number(button.dataset.pins));

                else if (button.dataset.ask !== undefined)
                    lookUp(Number(button.dataset.ask));

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
                    // where somebody presses Save after editing either - each
                    // with what it is held to, and with nothing of what the
                    // local controller only said about it.
                    servers:              servers.filter(server => server.address.trim().length > 0).map(entryOf),
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

                    ${certificatesView(result)}

                </div>
            `;

        }

        /**
         * What was made of the certificate of every server the test reached
         * over TLS or HTTPS - the most likely reason such a server answered
         * nothing, and the one most worth seeing.
         *
         * A test of all servers asks over the connections the local controller keeps
         * open, and a certificate is looked at when a connection is made: where
         * none was, that is said rather than nothing.
         */
        function certificatesView(result: DNSQueryResult): HTMLFragment {

            const judged = result.certificates ?? [];

            if (judged.length > 0)
                return html`
                    <h3>Certificates</h3>
                    ${judged.map(judgement => judgementView(judgement))}
                `;

            if (result.certificates !== undefined && !result.asked &&
                (current?.servers ?? []).some(server => isEncrypted(server.transport)))
                return html`
                    <p class="muted small">
                        No certificate was looked at: the connections to the name servers over TLS or HTTPS
                        were open already, and a certificate is looked at when a connection is made. The
                        Test of a server's own row makes a new one.
                    </p>
                `;

            return html``;

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
                                    Asked the way this local controller asks for anything: all of the configured
                                    servers at once, and the first usable answer wins. If none of them
                                    answers at all, that takes ${allServersTake(current)} seconds to say so.
                                `
                              : html`
                                    Asked of <code>${asking.address.includes(':')
                                                          ? `[${asking.address}]:${asking.port}`
                                                          : `${asking.address}:${asking.port}`}</code> over
                                    ${asking.transport} and of nothing else, and without the cache - an
                                    answer somebody else already fetched says nothing about this server.
                                    One that does not answer at all takes
                                    ${oneServerTakes(current, server!)} seconds to say so.
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
                    // One server's patience when one is asked, and the
                    // longest of them when all are: they are asked at once,
                    // so it does not add up.
                    asked = await api.dns.query(testName,
                                                testTypes,
                                                server === null
                                                    ? allServersTake(current)
                                                    : oneServerTakes(current, server),
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
        // looks untouched - so it is compared with what the local controller last said,
        // both as the local controller would be told them: a pin changed in a dialog
        // and changed back is no change, whichever order its keys came in.
        const release = unsaved.heldBy(
                            () => typedSinceDrawn(content.querySelector('#dns-form')) ||
                                  JSON.stringify(servers.map(entryOf)) !== JSON.stringify((current?.servers ?? []).map(entryOf))
                        );

        void load();

        return () => { cancelled = true; release(); };

    }

};
