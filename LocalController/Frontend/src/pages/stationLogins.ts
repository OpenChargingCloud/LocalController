import { api, type AuthMethod, type LoginGroup, type StationLogin, type StationLogins } from '../api/client';
import { auth } from '../auth';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, field, formatTimestamp } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';

/**
 * Who may sign in to the charging station server, with what, and under which
 * group's rules.
 *
 * The page is built around one thing being true: a credential can be shown
 * exactly once. A password is kept as a hash and cannot be read back at all; a
 * TOTP shared secret has to stay readable for the controller to compute tokens
 * from, but it is still never put into the list this page reads. So both are
 * handed out at the moment they are set and never again, and the page says so
 * where somebody would otherwise go looking for them.
 */
export const stationLoginsPage: Page = {

    title: 'Logins and groups',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/ocpp-server/logins',
            title:     'Logins and groups',
            subtitle:  'Which charging stations may sign in, with what, and what their group allows them.',
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
        let store: StationLogins | null = null;

        /** The credential that was just set, offered once and then gone. */
        let justMade: { id: string; what: string; secret: string } | null = null;

        /** The group whose settings are open, or null while none is. */
        let editing: string | null = null;


        function draw(): void {

            if (store === null)
                return;

            const logins = store;

            render(content, html`

                ${mayChange ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at the logins
                        but not change them.
                    </div>
                `}

                ${justMade === null ? '' : html`
                    <div class="notice ok">
                        <strong>The ${justMade.what} of '${justMade.id}' is</strong>
                        <code class="password">${justMade.secret}</code><br />
                        Write it down or type it into the charging station now. This is the only place it is
                        handed out - the list below never carries it.
                    </div>
                `}

                <div class="cards">
                    ${groupsCard(logins)}
                    ${loginsCard(logins)}
                </div>
            `);

            wire();

        }


        // What a group allows its members, said once for a whole site.
        function groupsCard(logins: StationLogins): HTMLFragment {

            return html`
                <section class="card wide">

                    <h2><i class="fa-solid fa-layer-group"></i> Login groups</h2>

                    <p class="hint">
                        A group says how its members may prove who they are and which OCPP security profiles they
                        may come in on. It can only narrow what
                        <a href="/configuration/ocpp-server">the server itself</a> allows, never widen it - and a
                        group with nothing ticked lets nobody in, which is how a whole site is stopped in one move.
                    </p>

                    <table class="table">
                        <thead>
                            <tr>
                                <th>Group</th>
                                <th>Accepts</th>
                                <th>Profiles</th>
                                <th>Members</th>
                                <th>On</th>
                                <th></th>
                            </tr>
                        </thead>
                        <tbody>
                            ${logins.groups.map(group => groupRow(group))}
                        </tbody>
                    </table>

                    ${editing === null ? '' : groupForm(logins.groups.find(group => group.id === editing) ?? null)}

                    ${editing !== null || !mayChange ? '' : html`
                        <div class="form-actions">
                            <button type="button" id="group-new" class="btn"
                                    ${logins.groups.length >= logins.maxGroups ? html`disabled` : ''}>
                                Make a group
                            </button>
                        </div>
                    `}

                </section>
            `;

        }


        function groupRow(group: LoginGroup): HTMLFragment {

            return html`
                <tr class="${group.enabled ? '' : 'dimmed'}">

                    <td>
                        <code>${group.id}</code>
                        ${group.builtIn ? html`<span class="badge">built in</span>` : ''}
                        <div class="small muted">${group.name}${group.note ? ` - ${group.note}` : ''}</div>
                    </td>

                    <td>
                        ${group.authMethods.length === 0
                              ? html`<span class="badge warn">nothing</span>`
                              : group.authMethods.map(method => html`<span class="badge">${method}</span> `)}
                    </td>

                    <td>
                        ${group.securityProfiles.length === 0
                              ? html`<span class="badge warn">none</span>`
                              : group.securityProfiles.map(profile => html`<span class="badge">${profile}</span> `)}
                    </td>

                    <td>${group.members}</td>

                    <td>${group.enabled ? 'yes' : html`<span class="warn">no</span>`}</td>

                    <td class="right">
                        <button type="button" class="btn small group-edit" data-id="${group.id}"
                                ${mayChange ? '' : html`disabled`}>
                            Settings
                        </button>
                        <button type="button" class="btn small danger group-remove" data-id="${group.id}"
                                ${mayChange && !group.builtIn && group.members === 0 ? '' : html`disabled`}>
                            Remove
                        </button>
                    </td>

                </tr>
            `;

        }


        // Making a group and changing one are the same form; a new one simply
        // starts empty and lets the identification be typed.
        function groupForm(group: LoginGroup | null): HTMLFragment {

            const ticked = (method: AuthMethod) => group?.authMethods.includes(method) ?? true;
            const onProfile = (profile: number) => group?.securityProfiles.includes(profile) ?? true;

            return html`
                <form id="group-form" class="form-stack" data-id="${group?.id ?? ''}">

                    <h3>${group === null ? 'A new group' : `The group '${group.id}'`}</h3>

                    ${group !== null ? '' : html`
                        <label>Identification
                            <input type="text" name="id" placeholder="field-test" maxlength="32" required
                                   pattern="[a-z0-9-]+" />
                            <span class="hint">Lower-case letters, digits and hyphens.</span>
                        </label>
                    `}

                    <label>Name
                        <input type="text" name="name" maxlength="60" value="${group?.name ?? ''}"
                               placeholder="Field test" />
                    </label>

                    <label>What it is
                        <input type="text" name="note" maxlength="200" value="${group?.note ?? ''}" />
                    </label>

                    <fieldset>
                        <legend>Ways in it accepts</legend>
                        <label class="check">
                            <input type="checkbox" name="basic" ${ticked('basic') ? html`checked` : ''} />
                            <span>A password (HTTP Basic Authentication)</span>
                        </label>
                        <label class="check">
                            <input type="checkbox" name="totp" ${ticked('totp') ? html`checked` : ''} />
                            <span>A one-time token (HTTP TOTP Authentication)</span>
                        </label>
                        <label class="check">
                            <input type="checkbox" name="certificate" ${ticked('certificate') ? html`checked` : ''} />
                            <span>A TLS client certificate</span>
                        </label>
                    </fieldset>

                    <fieldset>
                        <legend>OCPP security profiles it accepts</legend>
                        <label class="check">
                            <input type="checkbox" name="profile1" ${onProfile(1) ? html`checked` : ''} />
                            <span>1 - a password on an unencrypted port</span>
                        </label>
                        <label class="check">
                            <input type="checkbox" name="profile2" ${onProfile(2) ? html`checked` : ''} />
                            <span>2 - a password over TLS</span>
                        </label>
                        <label class="check">
                            <input type="checkbox" name="profile3" ${onProfile(3) ? html`checked` : ''} />
                            <span>3 - a TLS client certificate</span>
                        </label>
                    </fieldset>

                    <label class="switch">
                        <input type="checkbox" name="enabled" ${group?.enabled ?? true ? html`checked` : ''} />
                        <span>Its members may sign in</span>
                    </label>

                    <div class="form-actions">
                        <button type="submit" class="btn primary">Save the group</button>
                        <button type="button" id="group-cancel" class="btn">Cancel</button>
                        <span id="group-error" class="form-error" role="alert"></span>
                    </div>

                </form>
            `;

        }


        // Who may sign in, and with what.
        function loginsCard(logins: StationLogins): HTMLFragment {

            return html`
                <section class="card wide">

                    <h2><i class="fa-solid fa-charging-station"></i> Charging station logins</h2>

                    <p class="hint">
                        A charging station that is not in this list cannot sign in with a password or a token,
                        whatever it calls itself. A station that comes in on a client certificate does not need to
                        be listed - but listing it puts it into a group, which is the only way to say anything
                        about it. Kept in ${logins.file}.
                    </p>

                    ${logins.stations.length === 0
                          ? html`<p class="muted">No charging station may sign in yet.</p>`
                          : html`
                              <table class="table">
                                  <thead>
                                      <tr>
                                          <th>Identification</th>
                                          <th>What it is</th>
                                          <th>Group</th>
                                          <th>Signs in with</th>
                                          <th>Added</th>
                                          <th>May sign in</th>
                                          <th></th>
                                      </tr>
                                  </thead>
                                  <tbody>
                                      ${logins.stations.map(station => loginRow(station, logins))}
                                  </tbody>
                              </table>
                          `}

                    <form id="station-form" class="form-row">

                        <label>Identification
                            <input type="text" name="id" placeholder="cs001" maxlength="48"
                                   ${mayChange ? '' : html`disabled`} required />
                        </label>

                        <label>What it is
                            <input type="text" name="note" placeholder="Ladepunkt 1" maxlength="200"
                                   ${mayChange ? '' : html`disabled`} />
                        </label>

                        <label>Group
                            <select name="group" ${mayChange ? '' : html`disabled`}>
                                ${logins.groups.map(group => html`
                                    <option value="${group.id}"
                                            ${group.id === logins.defaultGroup ? html`selected` : ''}>
                                        ${group.name}
                                    </option>
                                `)}
                            </select>
                        </label>

                        <label>Password
                            <input type="text" name="password" placeholder="leave empty to make one up"
                                   minlength="${logins.minPasswordLength}" maxlength="64"
                                   ${mayChange ? '' : html`disabled`} />
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ${mayChange ? '' : html`disabled`}>
                                Add or change
                            </button>
                            <span id="station-error" class="form-error" role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        function loginRow(station: StationLogin, logins: StationLogins): HTMLFragment {

            const group = logins.groups.find(one => one.id === station.group);

            // What the station carries is one thing; what its group still
            // accepts is another. Showing only the first would be a page that
            // says "password" beside a station that cannot use one.
            const usable = (method: AuthMethod) => group?.enabled === true && group.authMethods.includes(method);

            return html`
                <tr class="${station.enabled && group?.enabled !== false ? '' : 'dimmed'}">

                    <td><code>${station.id}</code></td>

                    <td>${station.note ?? '-'}</td>

                    <td>
                        <select class="station-group" data-id="${station.id}" ${mayChange ? '' : html`disabled`}>
                            ${logins.groups.map(one => html`
                                <option value="${one.id}" ${one.id === station.group ? html`selected` : ''}>
                                    ${one.name}
                                </option>
                            `)}
                        </select>
                        ${group?.enabled === false ? html`<div class="small warn">the group is switched off</div>` : ''}
                    </td>

                    <td>
                        ${station.hasPassword
                              ? html`<span class="badge ${usable('basic') ? '' : 'warn'}">password</span> `
                              : ''}
                        ${station.hasTOTP
                              ? html`<span class="badge ${usable('totp') ? '' : 'warn'}">token</span> `
                              : ''}
                        ${!station.hasPassword && !station.hasTOTP
                              ? html`<span class="badge">certificate only</span>`
                              : ''}
                        ${station.totp
                              ? html`<div class="small muted">
                                         ${station.totp.length} characters of ${station.totp.hashAlgorithm},
                                         every ${station.totp.validitySeconds}s
                                     </div>`
                              : ''}
                    </td>

                    <td class="small muted">${formatTimestamp(station.addedAt)}</td>

                    <td>
                        <label class="switch small">
                            <input type="checkbox" class="station-enabled" data-id="${station.id}"
                                   ${station.enabled ? html`checked` : ''}
                                   ${mayChange ? '' : html`disabled`} />
                            <span>${station.enabled ? 'yes' : 'no'}</span>
                        </label>
                    </td>

                    <td class="right">
                        <button type="button" class="btn small station-totp" data-id="${station.id}"
                                ${mayChange ? '' : html`disabled`}>
                            ${station.hasTOTP ? 'New token secret' : 'Give it a token'}
                        </button>
                        ${station.hasTOTP ? html`
                            <button type="button" class="btn small station-totp-remove" data-id="${station.id}"
                                    ${mayChange ? '' : html`disabled`}>
                                Drop token
                            </button>
                        ` : ''}
                        ${station.hasPassword ? html`
                            <button type="button" class="btn small station-password-remove" data-id="${station.id}"
                                    ${mayChange ? '' : html`disabled`}>
                                Drop password
                            </button>
                        ` : ''}
                        <button type="button" class="btn small danger station-remove" data-id="${station.id}"
                                ${mayChange ? '' : html`disabled`}>
                            Remove
                        </button>
                    </td>

                </tr>
            `;

        }


        function wire(): void {

            content.querySelectorAll<HTMLButtonElement>('.group-edit').forEach(button => {
                button.addEventListener('click', () => { editing = button.dataset.id ?? null; draw(); });
            });

            const makeOne = content.querySelector<HTMLButtonElement>('#group-new');
            makeOne?.addEventListener('click', () => { editing = ''; draw(); });

            const cancel = content.querySelector<HTMLButtonElement>('#group-cancel');
            cancel?.addEventListener('click', () => { editing = null; draw(); });

            content.querySelector<HTMLFormElement>('#group-form')?.addEventListener('submit', event => {
                event.preventDefault();
                void saveGroup(event.target as HTMLFormElement);
            });

            content.querySelectorAll<HTMLButtonElement>('.group-remove').forEach(button => {
                button.addEventListener('click', () => void removeGroup(button.dataset.id ?? ''));
            });

            if (!mayChange)
                return;

            must<HTMLFormElement>(content, '#station-form').addEventListener('submit', event => {
                event.preventDefault();
                const form = event.target as HTMLFormElement;
                void addStation(field(form, 'id'), field(form, 'password', false),
                                field(form, 'group'), field(form, 'note', false));
            });

            content.querySelectorAll<HTMLInputElement>('.station-enabled').forEach(box => {
                box.addEventListener('change', () => void enable(box.dataset.id ?? '', box.checked));
            });

            content.querySelectorAll<HTMLSelectElement>('.station-group').forEach(chooser => {
                chooser.addEventListener('change', () => void move(chooser.dataset.id ?? '', chooser.value));
            });

            content.querySelectorAll<HTMLButtonElement>('.station-totp').forEach(button => {
                button.addEventListener('click', () => void giveAToken(button.dataset.id ?? ''));
            });

            content.querySelectorAll<HTMLButtonElement>('.station-totp-remove').forEach(button => {
                button.addEventListener('click', () => void dropToken(button.dataset.id ?? ''));
            });

            content.querySelectorAll<HTMLButtonElement>('.station-password-remove').forEach(button => {
                button.addEventListener('click', () => void dropPassword(button.dataset.id ?? ''));
            });

            content.querySelectorAll<HTMLButtonElement>('.station-remove').forEach(button => {
                button.addEventListener('click', () => void removeStation(button.dataset.id ?? ''));
            });

        }


        async function saveGroup(form: HTMLFormElement): Promise<void> {

            const error = content.querySelector<HTMLElement>('#group-error');

            if (error)
                error.textContent = '';

            const existing = form.dataset.id ?? '';
            const ticked   = (name: string) => form.querySelector<HTMLInputElement>(`[name="${name}"]`)?.checked === true;

            const methods: AuthMethod[] = [];

            if (ticked('basic'))        methods.push('basic');
            if (ticked('totp'))         methods.push('totp');
            if (ticked('certificate'))  methods.push('certificate');

            const profiles: number[] = [];

            if (ticked('profile1'))  profiles.push(1);
            if (ticked('profile2'))  profiles.push(2);
            if (ticked('profile3'))  profiles.push(3);

            try
            {

                store = await api.ocppServer.groups.save({
                    id:                existing.length > 0 ? existing : field(form, 'id'),
                    name:              field(form, 'name', false),
                    enabled:           ticked('enabled'),
                    authMethods:       methods,
                    securityProfiles:  profiles,
                    note:              field(form, 'note', false)
                });

                if (cancelled)
                    return;

                editing = null;
                draw();

            }
            catch (problem)
            {
                if (error && !cancelled)
                    error.textContent = errorMessage(problem);
            }

        }


        async function removeGroup(id: string): Promise<void> {

            if (!window.confirm(`Remove the login group '${id}'?`))
                return;

            await change(() => api.ocppServer.groups.remove(id));

        }


        async function addStation(id: string, password: string, group: string, note: string): Promise<void> {

            const error = must<HTMLElement>(content, '#station-error');
            error.textContent = '';

            try
            {

                const answer = await api.ocppServer.stations.save(id, password, group, note);

                if (cancelled)
                    return;

                store     = answer.stations;
                justMade  = answer.password === undefined
                                ? null
                                : { id, what: 'password', secret: answer.password };

                draw();

            }
            catch (problem)
            {
                if (!cancelled)
                    error.textContent = errorMessage(problem);
            }

        }


        async function giveAToken(id: string): Promise<void> {

            if (!window.confirm(
                    `Give '${id}' a new shared secret for one-time tokens?\n\n` +
                    'Any secret it has now stops working the moment this is saved.'))
                return;

            try
            {

                // Everything but the secret is left at what both ends already
                // agree on. A page that asked for the alphabet before it asked
                // for anything else would be a page nobody gets through.
                const answer = await api.ocppServer.stations.saveTOTP(id, {});

                if (cancelled)
                    return;

                store     = answer.stations;
                justMade  = answer.sharedSecret === undefined
                                ? null
                                : { id, what: 'TOTP shared secret', secret: answer.sharedSecret };

                draw();

            }
            catch (problem)
            {
                if (!cancelled)
                    window.alert(errorMessage(problem));
            }

        }


        async function dropToken(id: string): Promise<void> {

            if (!window.confirm(`Stop letting '${id}' in with a one-time token?`))
                return;

            await change(() => api.ocppServer.stations.removeTOTP(id));

        }


        async function dropPassword(id: string): Promise<void> {

            if (!window.confirm(`Stop letting '${id}' in with a password?`))
                return;

            await change(() => api.ocppServer.stations.removePassword(id));

        }


        async function removeStation(id: string): Promise<void> {

            if (!window.confirm(`Remove the charging station '${id}'? It will no longer be able to sign in.`))
                return;

            await change(() => api.ocppServer.stations.remove(id));

        }


        const enable = (id: string, wanted: boolean) =>
            change(() => api.ocppServer.stations.enable(id, wanted));

        const move = (id: string, group: string) =>
            change(() => api.ocppServer.stations.move(id, group));


        /** Every change that answers with the whole list and has nothing to hand out. */
        async function change(what: () => Promise<StationLogins>): Promise<void> {

            try
            {

                const answer = await what();

                if (cancelled)
                    return;

                store     = answer;
                justMade  = null;

                draw();

            }
            catch (problem)
            {
                if (!cancelled)
                {
                    window.alert(errorMessage(problem));
                    void load();
                }
            }

        }


        async function load(): Promise<void> {

            render(content, html`<div class="loading">Loading ...</div>`);

            try
            {

                const logins = await api.ocppServer.stations.get();

                if (cancelled)
                    return;

                store = logins;

                draw();

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`<div class="error-box">${errorMessage(problem)}</div>`);
            }

        }


        // The group being edited and the station being added are drafts. The
        // switches and choosers in the list are in no form: they take effect
        // the moment they are touched.
        const release = unsaved.heldBy(() => Array.from(content.querySelectorAll<HTMLFormElement>('form')).
                                                   some(form => typedSinceDrawn(form)));

        void load();

        return () => { cancelled = true; release(); };

    }

};
