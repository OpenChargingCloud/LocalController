import { api, type Clock, type NTSConfiguration, type NTSServerEntry, type NTSServerResult, type NTSSyncResult, type NTSTimeSource, type NTSUpdate, type TimeServerTest } from '../api/client';
import { auth } from '../auth';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, formatValue, humanizeKey, whileSaving } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';
import { entryOf, nameTaken, readable, withServer, withoutServer, type UsualPorts } from './ntsServers';

/**
 * What the NTS client allows itself when the local controller has not been told.
 *
 * The local controller's answer carries the timeout it was configured with, and null
 * where it was configured with none - and it does not repeat what the client
 * then falls back to, which is three seconds. This is only used to work out
 * how long this page waits for a test, and the page allows the local controller
 * fifteen seconds on top of it, so being wrong here by a few seconds costs
 * nothing at all.
 */
const theClientsOwnTimeout = 3;


/**
 * Where this local controller reads the time: its time servers, and the rules for
 * believing them.
 *
 * The page is the group, because the group is what the clock is checked
 * against. It used to lead with a form for one server "for the detailed test"
 * - and saving that form told the local controller a lone host name, which it
 * read as a group of one: four servers went down to the one in the form. Now
 * each server is a row with a Test of its own and an Edit, the group is added
 * to at the end of its list, and what the group is held to is a form of its
 * own below it.
 *
 * The cards stand one under the other, read from the top down: what the clock
 * is worth, whether, who, by which rules - and last "Sync now", which puts all
 * of that to work, with what came of it.
 *
 * Every change takes effect at once, and the local controller is told the whole list
 * each time - which is why the list is only ever changed by exactly one server
 * at a time, from a dialog, and why a change the local controller refuses leaves the
 * page as it was.
 *
 * The parts are drawn separately. "Sync now" redraws its own card, the clock
 * and the list, and a server saved the list, and nothing else - so that what
 * somebody was typing into the group's policy is still there when the answer
 * arrives.
 */
export const ntsPage: Page = {

    title: 'NTS client',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/nts',
            title:     'NTS client',
            subtitle:  'Where this local controller reads the time, and how it knows the answer is real.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        // Reload throws a draft away just as thoroughly as "Discard changes"
        // does, and from the opposite corner of the screen, so it asks first.
        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void load();
        });

        const mayChange = auth.can('changeNetworkSettings');
        const mayTest   = auth.can('runDiagnostics');

        let cancelled = false;
        let current: NTSConfiguration | null = null;
        let clock:   Clock            | null = null;
        let syncing = false;


        /** The ports a server is asked on unless its entry says otherwise. */
        function usualPorts(): UsualPorts {
            return {
                ntsKE:  current?.limits.defaultNTSKEPort ?? 4460,
                ntp:    current?.limits.defaultNTPPort   ?? 123
            };
        }

        /** The list as the local controller has it, as entries it can be told again. */
        function entries(): NTSServerEntry[] {
            const usual = usualPorts();
            return (current?.timeSources ?? []).map(source => entryOf(source, usual));
        }


        /** The whole page, from what the local controller last said. */
        function draw(): void {

            if (current === null)
                return;

            const configuration = current;

            render(content, html`

                ${mayChange ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at the time
                        servers but not change them. That needs the CPO or the system administrator role.
                    </div>
                `}

                <div class="cards stacked">

                    <section class="card" id="nts-clock"></section>

                    <section class="card" id="nts-switch"></section>

                    <section class="card" id="nts-servers"></section>

                    <section class="card" id="nts-policy"></section>

                    <section class="card">
                        <h2><i class="fa-solid fa-scale-balanced"></i> Cookie pool policy</h2>

                        <p class="hint">
                            What any new client starts with. Fixed in the client, not in the configuration.
                        </p>
                        <div class="kv-list">
                            ${Object.entries(configuration.policy).map(([key, value]) => html`
                                <div class="kv">
                                    <span class="k">${humanizeKey(key)}</span>
                                    <span class="v">${formatValue(value)}</span>
                                </div>
                            `)}
                        </div>
                    </section>

                    <section class="card" id="nts-sync"></section>

                </div>

            `);

            drawClock();
            drawSwitch();
            drawServers();
            drawPolicy();
            drawSync();

            wire();

        }


        /**
         * What time it is here and what that is worth.
         *
         * Every word of the verdict comes from the controller: whether a time
         * may be called legal depends on a claim its operator made and on how
         * the last check went, and a page that worked that out for itself
         * would sooner or later say "legal" where the controller says nothing
         * of the kind.
         */
        function drawClock(): void {

            const card = must<HTMLElement>(content, '#nts-clock');

            card.hidden = clock === null;

            if (clock === null)
                return;

            const now = clock;

            const why: Record<string, string> = {
                notClaimed:   'nobody has said whose time these servers disseminate',
                ntsOff:       'the time client is switched off',
                neverChecked: 'the clock has not been checked yet',
                stale:        'the last check is too old to count',
                offBy:        'the clock is further out than the tolerance allows'
            };

            render(card, html`

                <h2>
                    <i class="fa-solid fa-hourglass-half"></i> The clock
                    <span class="chip ${now.legal ? 'ok' : 'warn'}">
                        ${now.legal ? 'legal time' : 'unverified'}
                    </span>
                </h2>

                <div class="kv-list">

                    <div class="kv">
                        <span class="k">Here, now</span>
                        <span class="v">${formatValue(now.now)} <span class="muted small">(this controller's own clock)</span></span>
                    </div>

                    <div class="kv">
                        <span class="k">Last checked</span>
                        <span class="v">
                            ${now.nts.checkedAt === null
                                  ? 'never'
                                  : html`${formatValue(now.nts.checkedAt)} <span class="muted small">against ${now.nts.lastServer ?? `group '${now.nts.group ?? '-'}'`}</span>`}
                        </span>
                    </div>

                    <div class="kv">
                        <span class="k">Off by</span>
                        <span class="v">${now.nts.offset_ms === null ? '-' : `${now.nts.offset_ms} ms`}</span>
                    </div>

                    <div class="kv">
                        <span class="k">Authority</span>
                        <span class="v">${now.authority ?? html`<span class="muted">none claimed</span>`}</span>
                    </div>

                </div>

                <p class="hint">
                    ${now.legal
                          ? html`
                              Checked against ${now.nts.lastServer ?? 'the time servers'} within the last
                              ${Math.round(now.maxAgeSeconds / 60)} minutes and within
                              ${now.toleranceSeconds} s of them, and the operator says they carry
                              the time of ${now.authority}.
                            `
                          : html`
                              Unverified: ${why[now.why ?? ''] ?? 'the controller did not say why'}. The
                              time above is still this controller's own clock and is still what everything
                              below it is told - it is only not something anybody may call legal.
                            `}
                </p>

            `);

        }


        /** Whether this local controller asks anybody at all. */
        function drawSwitch(): void {

            const configuration = current!;

            render(must<HTMLElement>(content, '#nts-switch'), html`

                <h2><i class="fa-solid fa-power-off"></i> Time synchronisation</h2>

                <label class="switch">
                    <input type="checkbox" id="enabled"
                           ${configuration.enabled ? html`checked` : ''}
                           ${mayChange ? '' : html`disabled`} />
                    <span>${configuration.enabled ? 'switched on' : 'switched off'}</span>
                </label>

                <p class="hint">
                    Switched off, this local controller asks its time servers nothing at all - "Sync now" and the
                    tests below are refused rather than quietly doing nothing.
                </p>

                <span id="switch-error" class="form-error" role="alert"></span>

            `);

        }


        /**
         * The synchronisation this page shows: the one just asked for, or the
         * last one the local controller remembers - and none at all while the next one
         * is being asked for, neither the verdict nor what each server said.
         * Left standing under the spinning button, the last one read as the
         * new answer.
         */
        function shownSync(): NTSSyncResult | null {
            return syncing ? null : current?.result ?? current?.lastSync ?? null;
        }


        /** The time servers, and what each of them said. */
        function drawServers(): void {

            const configuration = current!;
            const sources       = configuration.timeSources ?? [];
            const sync          = shownSync();
            const switchedOn    = sources.filter(source => source.enabled).length;

            render(must<HTMLElement>(content, '#nts-servers'), html`

                <h2><i class="fa-solid fa-users"></i> Time servers</h2>

                <div class="time-server-list">
                    ${sources.length === 0
                          ? html`<p class="muted small">No time server configured.</p>`
                          : sources.map((source, index) => serverView(source, index, whatItSaid(sync, source), sync !== null))}
                </div>

                <div class="form-actions">
                    <button type="button" id="add-server" class="btn" title="Add a time server"
                            aria-label="Add a time server" ${mayChange ? '' : html`disabled`}>
                        <i class="fa-solid fa-plus"></i>
                    </button>
                    <span class="hint">
                        ${configuration.group
                              ? html`
                                    At least ${configuration.group.minServers} of the ${switchedOn} switched on must
                                    answer, and a disagreement of ${configuration.group.maxDeviationSeconds} s or more is
                                    written down. Servers sharing a priority are asked together; a lower priority is
                                    asked first.
                                `
                              : ''}
                    </span>
                </div>

            `);

        }


        /** What the group is held to, as a form. */
        function drawPolicy(): void {

            const configuration = current!;
            const settings      = configuration.settings;
            const limits        = configuration.limits;
            const held          = configuration.group?.minServers ?? settings.minServers;
            const off           = mayChange ? '' : html`disabled`;

            render(must<HTMLElement>(content, '#nts-policy'), html`

                <h2><i class="fa-solid fa-sliders"></i> Group policy</h2>

                <form id="policy-form" class="form-stack">

                    <label>Servers that must answer
                        <input type="number" name="minServers" min="1" max="255" step="1"
                               value="${settings.minServers}" ${off} />
                        <span class="hint">
                            ${held < settings.minServers
                                  ? html`Held to ${held} for as long as only ${held} ${held === 1 ? 'is' : 'are'} switched on.`
                                  : html`Two is the fewest that notice a server which is wrong rather than away.`}
                        </span>
                    </label>

                    <label>Agreed deviation in seconds
                        <input type="number" name="maxDeviationSeconds" step="0.001"
                               min="${limits.minDeviation}" max="${limits.maxDeviation}"
                               value="${settings.maxDeviationSeconds}" ${off} />
                        <span class="hint">How far apart their answers may be before the disagreement is written into the log.</span>
                    </label>

                    <label>Check the clock every ... seconds
                        <input type="number" name="checkEverySeconds" step="1"
                               min="${limits.minCheckEvery}" max="${limits.maxCheckEvery}"
                               value="${settings.checkEverySeconds}" ${off} />
                    </label>

                    <label>Timeout of a test in seconds
                        <input type="number" name="timeoutSeconds" step="0.1" min="0.1" max="${limits.maxTimeout}"
                               value="${settings.timeoutSeconds ?? ''}" placeholder="${theClientsOwnTimeout}" ${off} />
                        <span class="hint">What a server's Test allows each step. "Sync now" asks the way the clock check does, with timeouts of its own.</span>
                    </label>

                    <div class="form-actions">
                        <button type="submit" class="btn primary" ${off}>Save</button>
                        <span id="policy-note"  class="form-notice" role="status"></span>
                        <span id="policy-error" class="form-error"  role="alert"></span>
                    </div>

                    <span class="hint">Saved to <span class="path">${configuration.file}</span>, and in effect at once.</span>

                </form>

            `);

        }


        /**
         * The button that asks all the servers, and what the group concluded.
         *
         * A card of its own at the end of the page, below everything it puts
         * to work: the servers, and the rules they are held to.
         */
        function drawSync(): void {

            const sync = shownSync();

            render(must<HTMLElement>(content, '#nts-sync'), html`

                <h2><i class="fa-solid fa-rotate"></i> Synchronisation</h2>

                <div class="sync-bar">
                    <button type="button" id="sync" class="btn primary large" ${mayTest && !syncing ? '' : html`disabled`}>
                        <i class="fa-solid fa-rotate ${syncing ? 'fa-spin' : ''}"></i>
                        ${syncing ? 'Asking the servers ...' : 'Sync now'}
                    </button>
                    <span class="hint">
                        ${mayTest
                              ? html`
                                    Asks every server that is switched on, the way the clock check does: a key
                                    exchange over TLS, then one authenticated NTP request each. Every step goes
                                    into the log. The clock of this local controller is not stepped by it.
                                `
                              : html`Asking the servers needs the CPO or the system administrator role.`}
                    </span>
                </div>

                <span id="sync-error" class="form-error" role="alert"></span>

                ${sync === null ? '' : verdictView(sync)}

            `);

        }


        /**
         * One time server: who it is, what it said last, and what can be done
         * with it.
         *
         * A server switched on can be missing from a synchronisation without
         * anything being wrong with it: the bands are asked in turn, and one
         * with a higher priority is not asked when those before it were
         * enough. Its row says so rather than showing nothing, which looked
         * like a server that had been forgotten.
         */
        function serverView(source:  NTSTimeSource,
                            index:   number,
                            said:    NTSServerResult | undefined,
                            synced:  boolean): HTMLFragment {

            const usual  = usualPorts();
            const ports  = source.ntsKEPort !== usual.ntsKE || source.ntpPort !== usual.ntp
                               ? `, ports ${source.ntsKEPort} and ${source.ntpPort}`
                               : '';

            return html`
                <div class="time-server-row ${source.enabled ? '' : 'off'}">

                    <div class="who">
                        <span class="name">${readable(source.hostname)}</span>
                        <span class="muted small">
                            priority ${source.priority}${ports}${source.enabled ? '' : html`, <strong>switched off</strong>`}
                        </span>
                    </div>

                    <div class="said">
                        ${said === undefined
                              ? synced && source.enabled
                                    ? html`<span class="muted">not asked in the last synchronisation</span>`
                                    : ''
                              : said.ok
                                    ? html`<span class="answer ok">${ms(said.offset_ms, true)}, round trip ${ms(said.roundTrip_ms)}</span>`
                                    : html`<span class="answer bad">${said.error ?? (said.authenticated === false
                                                                                           ? 'answered, but the answer did not authenticate'
                                                                                           : 'no answer')}</span>`}
                        <span class="muted small">
                            ${source.lastExchange
                                  ? html`${formatValue(source.cookies)} cookie(s), ${source.aeadAlgorithm ?? ''}, exchanged ${formatValue(source.lastExchange)}`
                                  : 'not asked yet'}
                        </span>
                    </div>

                    <div class="root-ca">
                        ${source.rootCA
                              ? html`
                                    <span class="ca-name" title="${source.rootCA.subject}">
                                        <span class="muted small">Root CA</span> ${source.rootCA.name}
                                    </span>
                                    <span class="fingerprint" title="SHA-256 fingerprint of the root CA">${fingerprintView(source.rootCA.fingerprint)}</span>
                                `
                              : html`<span class="muted small">Root CA: no key exchange yet</span>`}
                    </div>

                    <div class="actions">
                        <button type="button" class="btn small" data-test="${index}"
                                title="Ask this server, and only this one" ${mayTest ? '' : html`disabled`}>
                            <i class="fa-solid fa-list-check"></i> Test
                        </button>
                        <button type="button" class="btn small" data-edit="${index}" ${mayChange ? '' : html`disabled`}>
                            <i class="fa-solid fa-pen"></i> Edit
                        </button>
                    </div>

                </div>
            `;

        }


        /**
         * A fingerprint that breaks in the middle and nowhere else.
         *
         * Two halves of 32 read against a pinned fingerprint far better than a
         * line broken wherever the column happens to end - and at every width
         * the column has, they are the same two halves. The break is a <wbr>
         * rather than a space, so that copying it copies the fingerprint.
         */
        function fingerprintView(fingerprint: string): HTMLFragment {
            return html`${(fingerprint.match(/.{1,32}/g) ?? [fingerprint]).map(part => html`${part}<wbr>`)}`;
        }


        /** What the group concluded, in one line under the button that asked. */
        function verdictView(sync: NTSSyncResult): HTMLFragment {

            const group = sync.group;

            return html`
                <div class="query-result sync-verdict ${sync.ok ? 'ok' : 'bad'}">
                    <strong>${sync.ok ? 'Succeeded:' : 'Failed:'}</strong>
                    ${sync.ok && group
                          ? html`${group.answered} of ${sync.servers?.length ?? 0} server(s) answered
                                 (${group.required} required), offset ${ms(group.offset_ms, true)},
                                 spread ${ms(group.spread_ms)}`
                          : html`${sync.error ?? 'no reason was given'}`}
                    <span class="muted small">
                        - ${formatValue(sync.at)}${sync.runtime_ms ? `, ${sync.runtime_ms} ms` : ''}
                    </span>
                    ${group?.deviationExceeded
                          ? html`<div class="small">The servers disagree by more than the agreed deviation - the log says by how much.</div>`
                          : ''}
                </div>
            `;

        }


        /** What one server said in a synchronisation, if it was asked in it. */
        function whatItSaid(sync:    NTSSyncResult | null,
                            source:  NTSTimeSource): NTSServerResult | undefined {

            const name = readable(source.hostname).toLowerCase();

            return sync?.servers?.find(server => readable(server.hostname).toLowerCase() === name);

        }


        /**
         * Add a time server, or change or delete one, in a dialog.
         *
         * A dialog rather than fields in the row: a server has five things
         * that can be said about it, and the list is for reading which servers
         * there are. And the local controller is told the whole list when this is saved,
         * so the dialog is also where it becomes clear that exactly one server
         * is being changed.
         *
         * @param index  the server's place in the list, or null to add one.
         */
        function editServer(index: number | null): void {

            if (current === null)
                return;

            const configuration  = current;
            const list           = entries();
            const shown          = index === null ? null : configuration.timeSources?.[index] ?? null;
            const usual          = usualPorts();

            if (index !== null && shown === null)
                return;

            const dialog = document.createElement('dialog');

            dialog.className = 'test-dialog server-dialog';

            document.body.appendChild(dialog);

            /** Shut it and take it away - both, as not every browser fires "close". */
            const dismiss = (): void => { dialog.close(); dialog.remove(); };

            render(dialog, html`

                <h2><i class="fa-solid fa-clock"></i> ${shown === null ? 'A new time server' : readable(shown.hostname)}</h2>

                <form id="server-form" class="form-stack">

                    <label>Host name
                        <input type="text" name="hostname" placeholder="ptbtime1.ptb.de"
                               value="${shown === null ? '' : readable(shown.hostname)}" />
                        <span class="hint">
                            A name and not an address: the key exchange checks the server's TLS certificate
                            against it.
                        </span>
                    </label>

                    <label>Priority
                        <input type="number" name="priority" min="0" max="255" step="1"
                               value="${shown?.priority ?? 0}" />
                        <span class="hint">A lower priority is asked first; servers sharing one are asked together.</span>
                    </label>

                    <label>NTS-KE port
                        <input type="number" name="ntsKEPort" min="1" max="65535" placeholder="${usual.ntsKE}"
                               value="${shown !== null && shown.ntsKEPort !== usual.ntsKE ? shown.ntsKEPort : ''}" />
                    </label>

                    <label>NTP port
                        <input type="number" name="ntpPort" min="1" max="65535" placeholder="${usual.ntp}"
                               value="${shown !== null && shown.ntpPort !== usual.ntp ? shown.ntpPort : ''}" />
                        <span class="hint">Left empty, the usual ones: ${usual.ntsKE} and ${usual.ntp}.</span>
                    </label>

                    <label class="checkbox">
                        <input type="checkbox" name="enabled" ${shown === null || shown.enabled ? html`checked` : ''} />
                        Ask this server
                        <span class="hint">Switched off, it stays in the list and is not asked.</span>
                    </label>

                    <div class="form-actions">
                        <button type="submit" class="btn primary">Save</button>
                        <button type="button" class="btn" id="server-cancel">Cancel</button>
                        ${shown === null
                              ? ''
                              : html`<button type="button" class="btn danger" id="server-delete">
                                         <i class="fa-solid fa-trash"></i> Delete
                                     </button>`}
                        <span id="server-error" class="form-error" role="alert"></span>
                    </div>

                </form>

            `);

            const form   = must<HTMLFormElement>(dialog, '#server-form');
            const error  = must<HTMLElement>    (dialog, '#server-error');

            /**
             * Tell the local controller the list with the one change in it, and close
             * only when it took it. What it refuses - a server below the
             * quorum, the last one - is said in the dialog, and the list the
             * page shows is still the one the local controller has.
             */
            async function tell(servers: NTSServerEntry[]): Promise<void> {

                error.textContent = '';

                try
                {
                    current = await whileSaving(dialog, null, () => api.nts.save({ servers }));
                }
                catch (problem)
                {
                    error.textContent = errorMessage(problem);
                    return;
                }

                dismiss();

                if (!cancelled)
                    drawServers();

            }

            form.addEventListener('submit', event => {

                event.preventDefault();

                const data      = new FormData(form);
                const hostname  = readable(String(data.get('hostname') ?? '').trim());
                const ntsKE     = String(data.get('ntsKEPort') ?? '').trim();
                const ntp       = String(data.get('ntpPort')   ?? '').trim();
                const priority  = Number(data.get('priority')  ?? 0);

                if (hostname.length === 0) {
                    error.textContent = 'A host name is needed.';
                    return;
                }

                if (nameTaken(list, hostname, index)) {
                    error.textContent = `${hostname} is in the list already.`;
                    return;
                }

                const entry: NTSServerEntry = { hostname };

                if (priority !== 0)                                      entry.priority   = priority;
                if (ntsKE.length > 0 && Number(ntsKE) !== usual.ntsKE)  entry.ntsKEPort  = Number(ntsKE);
                if (ntp.length   > 0 && Number(ntp)   !== usual.ntp)    entry.ntpPort    = Number(ntp);
                if (data.get('enabled') === null)                        entry.enabled    = false;

                void tell(withServer(list, index, entry));

            });

            must<HTMLButtonElement>(dialog, '#server-cancel').addEventListener('click', dismiss);

            if (shown !== null && index !== null)
                must<HTMLButtonElement>(dialog, '#server-delete').addEventListener('click', () => {

                    if (!confirm(`Delete ${readable(shown.hostname)}?\n\n` +
                                 `It is taken out of the list, and out of ${configuration.file}.`))
                        return;

                    void tell(withoutServer(list, index));

                });

            dialog.addEventListener('close',  dismiss);
            dialog.addEventListener('cancel', dismiss);

            dialog.showModal();

            must<HTMLInputElement>(dialog, 'input[name="hostname"]').focus();

        }


        /**
         * Ask one time server everything, in a dialog, line by line.
         *
         * "Sync now" answers whether the group has a time; this answers where
         * one server got to, which is the question somebody has when it did
         * not. The steps are the ones the exchange actually has - the name, the
         * TCP connection, the TLS handshake, the key exchange, the
         * authenticated request - and each is timed, so a server that is merely
         * slow can be told from one that is refusing.
         *
         * @param host  which server, asked on the ports it is configured with.
         *              Sent as it is read, without the root's dot, because the
         *              local controller writes it into the log as it was sent.
         */
        async function testServer(host: string): Promise<void> {

            const dialog = document.createElement('dialog');

            dialog.className = 'test-dialog';

            render(dialog, html`
                <h2>Asking ${readable(host)}</h2>
                <div class="test-steps" id="test-steps">
                    <div class="loading">Name, key exchange, authenticated time request ...</div>
                </div>
                <div class="form-actions">
                    <button type="button" class="btn" id="test-close" disabled>Close</button>
                </div>
            `);

            document.body.appendChild(dialog);
            dialog.showModal();

            // Both halves explicitly: a browser the charging station's pages
            // were tried in fired no close event at all.
            const dismiss = (): void => { dialog.close(); dialog.remove(); };

            dialog.addEventListener('close',  dismiss);
            dialog.addEventListener('cancel', dismiss);

            const close = must<HTMLButtonElement>(dialog, '#test-close');

            close.addEventListener('click', dismiss);

            let result: TimeServerTest;

            try
            {
                result = await api.nts.test(current?.settings.timeoutSeconds ?? theClientsOwnTimeout, host);
            }
            catch (problem)
            {
                render(must<HTMLElement>(dialog, '#test-steps'), html`
                    <div class="error-box">The test could not be run: ${errorMessage(problem)}</div>
                `);
                close.disabled = false;
                close.focus();
                return;
            }

            render(must<HTMLElement>(dialog, '#test-steps'), html`
                <div class="${result.ok ? 'notice' : 'error-box'}">
                    ${result.ok
                          ? html`${readable(result.host)} answered. ${result.runtime_ms} ms altogether.`
                          : html`${readable(result.host)} did not answer. ${result.runtime_ms} ms altogether.`}
                </div>
                <ol class="test-log">
                    ${result.steps.map(step => html`
                        <li class="level-${step.level}">
                            <span class="at">+${step.at_ms} ms</span>
                            <span class="text">${step.text}</span>
                        </li>
                    `)}
                </ol>
            `);

            close.disabled = false;
            close.focus();

        }


        /**
         * Listen on the parts rather than on what is in them, because the
         * parts are redrawn one at a time and a listener on a button that was
         * redrawn away would be listening to nothing.
         */
        function wire(): void {

            must<HTMLElement>(content, '#nts-switch').addEventListener('change', event => {

                const box = event.target as HTMLInputElement;

                if (box.id === 'enabled')
                    void switchTo(box.checked);

            });

            must<HTMLElement>(content, '#nts-servers').addEventListener('click', event => {

                const target  = event.target as HTMLElement;
                const button  = target.closest<HTMLButtonElement>('button');

                if (button === null || button.disabled)
                    return;

                if (button.id === 'add-server')
                    editServer(null);

                else if (button.dataset.edit !== undefined)
                    editServer(Number(button.dataset.edit));

                else if (button.dataset.test !== undefined) {

                    const source = current?.timeSources?.[Number(button.dataset.test)];

                    if (source !== undefined)
                        void testServer(readable(source.hostname));

                }

            });

            must<HTMLElement>(content, '#nts-sync').addEventListener('click', event => {

                const button = (event.target as HTMLElement).closest<HTMLButtonElement>('#sync');

                if (button !== null && !button.disabled)
                    void runSync();

            });

            must<HTMLElement>(content, '#nts-policy').addEventListener('submit', event => {

                event.preventDefault();

                const form     = event.target as HTMLFormElement;
                const data     = new FormData(form);
                const timeout  = String(data.get('timeoutSeconds') ?? '').trim();

                const update: NTSUpdate = {
                    minServers:           Number(data.get('minServers')),
                    maxDeviationSeconds:  Number(data.get('maxDeviationSeconds')),
                    checkEverySeconds:    Number(data.get('checkEverySeconds'))
                };

                if (timeout.length > 0)
                    update.timeoutSeconds = Number(timeout);

                void savePolicy(update);

            });

        }


        /** Switch the whole thing on or off. */
        async function switchTo(on: boolean): Promise<void> {

            const error = must<HTMLElement>(content, '#switch-error');

            error.textContent = '';

            try
            {
                current = await whileSaving(must<HTMLElement>(content, '#nts-switch'), null,
                                            () => api.nts.save({ enabled: on }));

                // Whether the time may be called legal depends on it.
                clock   = await api.clock();
            }
            catch (problem)
            {
                // Back to what the local controller has, which is not what the box
                // says now that somebody has clicked it.
                drawSwitch();
                must<HTMLElement>(content, '#switch-error').textContent = errorMessage(problem);
                return;
            }

            drawSwitch();
            drawClock();

        }


        /** Tell the local controller what the group is held to. */
        async function savePolicy(update: NTSUpdate): Promise<void> {

            const policy  = must<HTMLElement>(content, '#nts-policy');
            const note    = must<HTMLElement>(content, '#policy-note');

            note.textContent = '';
            must<HTMLElement>(content, '#policy-error').textContent = '';

            try
            {
                current = await whileSaving(policy, note, () => api.nts.save(update));
            }
            catch (problem)
            {
                must<HTMLElement>(content, '#policy-error').textContent = errorMessage(problem);
                return;
            }

            // The list as well: what the group is held to is said under it.
            drawPolicy();
            drawServers();

            must<HTMLElement>(content, '#policy-note').textContent = 'Saved, and in effect.';

        }


        async function runSync(): Promise<void> {

            // Its own card for the button and the verdict, and the list for
            // what each server said.
            syncing = true;
            drawSync();
            drawServers();

            try
            {
                // The answer carries the whole configuration as well as the
                // result, because an exchange moves the cookies and the record
                // of the last key exchange that each row is showing.
                current = await api.nts.sync(current?.settings.timeoutSeconds ?? theClientsOwnTimeout);

                // And the clock: an exchange is exactly the thing that turns
                // "never checked" into a number.
                clock   = await api.clock();
            }
            catch (problem)
            {
                syncing = false;
                drawSync();
                drawServers();
                must<HTMLElement>(content, '#sync-error').textContent = errorMessage(problem);
                return;
            }

            syncing = false;
            drawSync();
            drawServers();
            drawClock();

        }


        async function load(): Promise<void> {

            try
            {
                // Together, because they are two halves of one question and a
                // page that showed the time servers without saying whether
                // their answers are any good has said nothing.
                const [loaded, now] = await Promise.all([api.nts.get(), api.clock()]);

                if (!cancelled) {
                    current = loaded;
                    clock   = now;
                    draw();
                }
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The NTS configuration could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        const release = unsaved.heldBy(() => typedSinceDrawn(content.querySelector('#policy-form')));

        void load();

        return () => { cancelled = true; release(); };

    }

};


/**
 * Milliseconds the way the local controller's log writes them: one place after a point,
 * and a sign where the number says which way.
 */
function ms(value: number | null | undefined, signed = false): string {

    if (value === null || value === undefined)
        return '-';

    const sign = signed ? (value < 0 ? '-' : '+') : '';

    return `${sign}${Math.abs(value).toFixed(1)} ms`;

}
