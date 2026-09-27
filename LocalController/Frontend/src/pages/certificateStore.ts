import { api, type Certificate, type CertificateKind, type CertificateStore } from '../api/client';
import { auth } from '../auth';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, whileSaving } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';

/**
 * The largest file this page will offer to import.
 *
 * A certificate chain is a few kilobytes; a megabyte is already somebody who
 * picked the wrong file. Refused here rather than at the local controller so
 * that the answer is immediate and the browser does not base64 a video first.
 */
const largestImport = 1024 * 1024;


/** How soon a certificate is worth warning about, in days. */
const expiringSoon = 30;


/** What a usage is called on this page: the service, in the words of the pages it is set on. */
const usageNames: Record<string, string> = {
    dns:  'name servers (DNS)',
    nts:  'time servers (NTS)'
};

function usageName(usage: string): string {
    return usageNames[usage] ?? usage;
}


/**
 * This local controller's own certificate store: the roots it believes of
 * the servers it connects to, what it presents in TLS itself, and the servers
 * it recognises.
 *
 * The vehicle's page, over the same store, with the kinds of TLS and none of
 * ISO 15118's. A **root** is believed: any number may be on at once, and
 * switching one off changes which chains are accepted from the next
 * connection on. An **identity** is presented, with its private key. The
 * **server certificates** are neither: kept so that a time server or a name
 * server can be held to one of them by its fingerprint, on the NTS and the DNS
 * page.
 *
 * A TLS root and a server certificate are told what they are for - the time
 * servers, the name servers, or every use - when they are uploaded, and can
 * be told again: a root uploaded for the name servers vouches for no time.
 *
 * Not the charging station port's certificates and the chains it accepts from
 * the stations, which have pages of their own.
 *
 * Certificates arrive two ways and both are first class. "Import" uploads a
 * file and copies it in; "Re-read the directory" picks up whatever somebody put
 * there by hand, which on a machine you already have a shell on is the shorter
 * path. Either way the store ends up the same, because the store is the
 * directory.
 */
export const certificateStorePage: Page = {

    title: 'Certificate store',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/certificates',
            title:     'Certificate store',
            subtitle:  'The roots this local controller believes of the servers it connects to, what it presents in TLS, and the servers it recognises.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').addEventListener('click', () => {
            if (unsaved.mayBeLost())
                void load();
        });

        const mayChange = auth.can('certificates', 'edit');

        let cancelled = false;
        let current: CertificateStore | null = null;
        let busy = false;


        function draw(): void {

            if (current === null)
                return;

            const store = current;

            render(content, html`

                ${store.keysAreUnencrypted ? html`
                    <div class="notice">
                        The private keys in this store are <strong>not encrypted</strong>. Anybody who can read
                        <code>${store.directory}</code> can take this local controller's identity.
                    </div>` : ''}

                <section class="card">
                    <h2><i class="fa-solid fa-certificate"></i> The store</h2>
                    <p class="hint">
                        One file per certificate below <code>${store.directory}</code>, with
                        <code>index.json</code> beside them recording what each one is called, whether it is
                        switched on and what it is kept for. Certificates already in that directory are read again
                        at every start, so copying one in is a way to install it.
                    </p>
                    <div class="form-actions">
                        <button type="button" id="rescan" class="btn" ${mayChange && !busy ? '' : html`disabled`}>
                            Re-read the directory
                        </button>
                        <span id="store-note"  class="form-notice" role="status"></span>
                        <span id="store-error" class="form-error"  role="alert"></span>
                    </div>
                </section>

                ${mayChange ? importCard() : ''}

                <h2>What this local controller believes</h2>
                <p class="hint">
                    Trust anchors: the roots a server this local controller connects to may chain to, beside the
                    ones this machine trusts. Every switched-on root is believed at once.
                </p>
                ${store.trustAnchors.map(kind => kindCard(kind))}

                <h2>What this local controller presents</h2>
                <p class="hint">
                    What it shows of itself in TLS, with its private key, where a server asks it for a certificate.
                </p>
                ${store.credentials.map(kind => kindCard(kind))}

                ${(store.recognised ?? []).length === 0 ? '' : html`
                    <h2>What this local controller recognises</h2>
                    <p class="hint">
                        Neither believed nor presented: the certificates of servers this local controller connects
                        to, kept so that a time server or a name server can be held to one of them by its
                        fingerprint - on the <a href="/configuration/nts">NTS</a> and the
                        <a href="/configuration/dns">DNS</a> page, where each server's dialog offers the ones kept
                        for it.
                    </p>
                    ${(store.recognised ?? []).map(kind => kindCard(kind))}
                `}

            `);

            wire();

        }


        /** The kinds in the order the page shows them, which is the order the import offers them in. */
        function kindsShown(): CertificateKind[] {
            const store = current!;
            return [ ...store.trustAnchors, ...store.credentials, ...(store.recognised ?? []) ];
        }

        /** Whether a certificate of this kind is told what it is for. */
        function hasUsages(kind: CertificateKind): boolean {
            return current?.kinds[kind]?.hasUsages === true;
        }

        /**
         * The boxes that say what a certificate is for, one per usage the
         * local controller knows - none ticked for every use, which is what a
         * certificate kept before there were usages is as well, and what the
         * controller would refuse to be told as an empty list.
         */
        function usagesFields(ticked: readonly string[] | null | undefined) {

            return html`
                ${(current!.usages ?? []).map(usage => html`
                    <label class="checkbox">
                        <input type="checkbox" name="usage" value="${usage}"
                               ${ticked?.includes(usage) ? html`checked` : ''} ${busy ? html`disabled` : ''} />
                        ${usageName(usage)}
                    </label>
                `)}
                <span class="hint">None ticked: for every use.</span>
            `;

        }


        /** The card that puts a new certificate on this local controller. */
        function importCard() {

            const store = current!;
            const first = kindsShown()[0];

            return html`
                <section class="card">
                    <h2><i class="fa-solid fa-file-import"></i> Import a certificate</h2>
                    <form id="import-form" class="form-stack">

                        <label>The file
                            <input type="file" name="file" id="import-file"
                                   accept=".pem,.crt,.cer,.der,.p12,.pfx" ${busy ? html`disabled` : ''} />
                        </label>
                        <p class="hint">
                            PEM, DER or PKCS#12, copied into the store rather than referenced where it is.
                            An identity this local controller <em>presents</em> has to bring its private key, so a
                            PEM for one holds the key beside the certificate - which is how
                            <code>openssl</code> writes a whole credential into one file.
                        </p>

                        <label>What it is for
                            <select name="kind" id="import-kind" ${busy ? html`disabled` : ''}>
                                ${kindsShown().map(kind => html`
                                    <option value="${kind}">${store.kinds[kind].description}</option>
                                `)}
                            </select>
                        </label>

                        <fieldset class="usages" id="import-usages" ${first !== undefined && hasUsages(first) ? '' : html`hidden`}>
                            <legend>What it is kept for</legend>
                            ${usagesFields(null)}
                        </fieldset>

                        <label>What opens it, if it is a protected PKCS#12
                            <input type="password" name="password" autocomplete="off" ${busy ? html`disabled` : ''} />
                        </label>
                        <p class="hint">
                            Used once, to read the file. The store keeps what it holds without a password, so this
                            is not written down anywhere.
                        </p>

                        <label>What to call it
                            <input type="text" name="label" maxlength="120" placeholder="its common name"
                                   ${busy ? html`disabled` : ''} />
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ${busy ? html`disabled` : ''}>Import</button>
                            <span id="import-note"  class="form-notice" role="status"></span>
                            <span id="import-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>
                </section>
            `;

        }


        /** One kind, and everything in the store of that kind. */
        function kindCard(kind: CertificateKind) {

            const store    = current!;
            const entries  = store.certificates[kind] ?? [];

            return html`
                <section class="card">
                    <h3>${store.kinds[kind].description}</h3>

                    ${entries.length === 0
                        ? html`<p class="hint">None.</p>`
                        : html`
                          <div class="table-scroll">
                            <table class="records">
                                <thead>
                                    <tr>
                                        <th>Name</th>
                                        <th>Subject</th>
                                        <th>Key</th>
                                        <th>Valid until</th>
                                        <th>State</th>
                                        <th></th>
                                    </tr>
                                </thead>
                                <tbody>
                                    ${entries.map(entry => row(entry))}
                                </tbody>
                            </table>
                          </div>
                        `}

                </section>
            `;

        }


        /** One certificate. */
        function row(entry: Certificate) {

            const days = Math.floor((new Date(entry.notAfter).getTime() - Date.now()) / 86400000);

            const state = entry.expired        ? html`<span class="chip bad">expired</span>`
                        : entry.notYetValid    ? html`<span class="chip warn">not yet valid</span>`
                        : !entry.active        ? html`<span class="chip">switched off</span>`
                        : days <= expiringSoon ? html`<span class="chip warn">${days} day(s) left</span>`
                        :                        html`<span class="chip">on</span>`;

            return html`
                <tr>
                    <td>
                        ${entry.label}
                        <br /><code class="muted" title="SHA-256: ${entry.thumbprint}">${entry.id}</code>
                        ${hasUsages(entry.kind)
                              ? html`<br /><span class="chips usages-of">
                                         ${entry.usages === null || entry.usages === undefined
                                               ? html`<span class="chip">for every use</span>`
                                               : entry.usages.map(usage => html`<span class="chip">${usageName(usage)}</span>`)}
                                     </span>`
                              : ''}
                    </td>
                    <td>
                        ${entry.subject}
                        ${entry.chainLength > 0 ? html`<br /><span class="muted">+${entry.chainLength} sub-CA(s)</span>` : ''}
                    </td>
                    <td>
                        ${entry.keyAlgorithm}
                        ${entry.hasPrivateKey ? html`<br /><span class="muted">with private key</span>` : ''}
                    </td>
                    <td>${new Date(entry.notAfter).toISOString().slice(0, 10)}</td>
                    <td>${state}</td>
                    <td>
                        <button type="button" class="btn small" data-toggle="${entry.id}"
                                ${mayChange && !busy ? '' : html`disabled`}>
                            ${entry.active ? 'Switch off' : 'Switch on'}
                        </button>
                        <button type="button" class="btn small" data-rename="${entry.id}"
                                ${mayChange && !busy ? '' : html`disabled`}>
                            Rename
                        </button>
                        ${hasUsages(entry.kind)
                              ? html`<button type="button" class="btn small" data-usages="${entry.id}"
                                             ${mayChange && !busy ? '' : html`disabled`}>
                                         Uses
                                     </button>`
                              : ''}
                        <button type="button" class="btn small danger" data-remove="${entry.id}"
                                ${mayChange && !busy ? '' : html`disabled`}>
                            Delete
                        </button>
                    </td>
                </tr>
            `;

        }


        function wire(): void {

            must<HTMLButtonElement>(content, '#rescan').addEventListener('click', () => { void rescan(); });

            const form = content.querySelector<HTMLFormElement>('#import-form');

            form?.addEventListener('submit', event => {
                event.preventDefault();
                void doImport(form);
            });

            // What it is kept for is asked only of the kinds that are told it.
            content.querySelector<HTMLSelectElement>('#import-kind')?.addEventListener('change', event => {
                must<HTMLElement>(content, '#import-usages').hidden =
                    !hasUsages((event.target as HTMLSelectElement).value as CertificateKind);
            });

            for (const button of content.querySelectorAll<HTMLButtonElement>('[data-usages]'))
                button.addEventListener('click', () => { editUsages(button.dataset.usages!); });

            for (const button of content.querySelectorAll<HTMLButtonElement>('[data-toggle]'))
                button.addEventListener('click', () => { void toggle(button.dataset.toggle!); });

            for (const button of content.querySelectorAll<HTMLButtonElement>('[data-rename]'))
                button.addEventListener('click', () => { void rename(button.dataset.rename!); });

            for (const button of content.querySelectorAll<HTMLButtonElement>('[data-remove]'))
                button.addEventListener('click', () => { void remove(button.dataset.remove!); });

        }


        async function doImport(form: HTMLFormElement): Promise<void> {

            const note  = must<HTMLElement>(content, '#import-note');
            const error = must<HTMLElement>(content, '#import-error');

            note.textContent  = '';
            error.textContent = '';

            const chosen = must<HTMLInputElement>(content, '#import-file').files?.[0];

            if (chosen === undefined) {
                error.textContent = 'Choose a file first.';
                return;
            }

            if (chosen.size > largestImport) {
                error.textContent = `That file is ${Math.round(chosen.size / 1024)} kB, and a certificate is a few. ` +
                                     'This is almost certainly not the file you meant.';
                return;
            }

            const data     = new FormData(form);
            const kind     = data.get('kind') as CertificateKind;
            const password = String(data.get('password') ?? '');
            const label    = String(data.get('label')    ?? '').trim();
            const usages   = hasUsages(kind) ? data.getAll('usage').map(String) : [];

            busy = true;

            try
            {

                const imported = await whileSaving(content, note, async () => api.certificates.import({
                                           kind,
                                           content:  await base64Of(chosen),
                                           password: password.length > 0 ? password : undefined,
                                           label:    label.length    > 0 ? label    : undefined,
                                           // Left out for every use; the controller
                                           // refuses a certificate for no use.
                                           usages:   usages.length   > 0 ? usages   : undefined
                                       }));

                busy    = false;
                current = await api.certificates.get();
                draw();

                must<HTMLElement>(content, '#import-note').textContent =
                    `Imported ${imported.label}, and switched on.`;

            }
            catch (problem)
            {
                busy = false;
                draw();
                must<HTMLElement>(content, '#import-error').textContent = errorMessage(problem);
            }

        }


        async function toggle(id: string): Promise<void> {

            const entry = everything().find(one => one.id === id);

            if (entry === undefined)
                return;

            await change(() => api.certificates.update(id, { active: !entry.active }));

        }


        async function rename(id: string): Promise<void> {

            const entry = everything().find(one => one.id === id);

            if (entry === undefined)
                return;

            // The label is one of the things about a stored certificate that
            // are somebody's to decide; everything else on the row is read out
            // of the file and is not up for editing.
            const given = prompt('What should this certificate be called?\n\n' +
                                 'Leave it empty for its own common name.', entry.label);

            if (given === null)
                return;

            await change(() => api.certificates.update(id, { label: given.trim().length > 0 ? given.trim() : null }));

        }


        /**
         * Say again what a TLS root or a server certificate is for, in a
         * dialog.
         *
         * A dialog with a Save rather than boxes in the row that save on every
         * click: each change is a line in the log, and taking the last tick
         * away on the way to another one would have made the certificate one
         * for every use in between.
         */
        function editUsages(id: string): void {

            const entry = everything().find(one => one.id === id);

            if (entry === undefined)
                return;

            const dialog = document.createElement('dialog');

            dialog.className = 'test-dialog server-dialog';

            document.body.appendChild(dialog);

            /** Shut it and take it away - both, as not every browser fires "close". */
            const dismiss = (): void => { dialog.close(); dialog.remove(); };

            render(dialog, html`

                <h2><i class="fa-solid fa-certificate"></i> ${entry.label}</h2>

                <form id="usages-form" class="form-stack">

                    <fieldset class="usages">
                        <legend>What it is kept for</legend>
                        ${usagesFields(entry.usages)}
                    </fieldset>

                    <p class="hint">
                        ${entry.kind === 'tlsRoot'
                              ? html`A root kept for the time servers alone vouches for no name server, and the other
                                     way round - except for a server whose own entry names it: naming it there says
                                     the same, and more narrowly.`
                              : html`Offered in the dialog of the servers it is kept for, on the NTS and the DNS page.`}
                    </p>

                    <div class="form-actions">
                        <button type="submit" class="btn primary">Save</button>
                        <button type="button" class="btn" id="usages-cancel">Cancel</button>
                        <span id="usages-error" class="form-error" role="alert"></span>
                    </div>

                </form>

            `);

            const form = must<HTMLFormElement>(dialog, '#usages-form');

            form.addEventListener('submit', event => {

                event.preventDefault();

                const ticked = new FormData(form).getAll('usage').map(String);

                void (async () => {

                    try
                    {
                        // None ticked is every use again, which the controller
                        // is told as null: a list with nothing in it would be
                        // a certificate for no use, and is refused.
                        await whileSaving(dialog, null, () => api.certificates.update(id, { usages: ticked.length > 0 ? ticked : null }));
                    }
                    catch (problem)
                    {
                        must<HTMLElement>(dialog, '#usages-error').textContent = errorMessage(problem);
                        return;
                    }

                    dismiss();

                    // The store again rather than the one certificate the
                    // answer carries, as after every other change on this page.
                    await load();

                })();

            });

            must<HTMLButtonElement>(dialog, '#usages-cancel').addEventListener('click', dismiss);

            dialog.addEventListener('close',  dismiss);
            dialog.addEventListener('cancel', dismiss);

            dialog.showModal();

        }


        async function remove(id: string): Promise<void> {

            const entry = everything().find(one => one.id === id);

            if (entry === undefined)
                return;

            // The file goes with it, and there is no copy anywhere else. Asked
            // once, naming what is about to go.
            if (!confirm(`Delete ${entry.label}?\n\nIts file is deleted from the store as well, and ` +
                         `a certificate with a private key cannot be put back without that key.`))
                return;

            await change(() => api.certificates.remove(id));

        }


        /** Everything in the store, flattened - for finding one by its handle. */
        function everything(): Certificate[] {
            return Object.values(current?.certificates ?? {}).flat();
        }


        /** One change to the store, with the page held still and then redrawn from the answer. */
        async function change(doing: () => Promise<unknown>): Promise<void> {

            const note  = must<HTMLElement>(content, '#store-note');
            const error = must<HTMLElement>(content, '#store-error');

            note.textContent  = '';
            error.textContent = '';

            busy = true;

            try
            {
                await whileSaving(content, note, doing);
                busy    = false;
                current = await api.certificates.get();
                draw();
            }
            catch (problem)
            {
                busy = false;
                draw();
                must<HTMLElement>(content, '#store-error').textContent = errorMessage(problem);
            }

        }


        async function rescan(): Promise<void> {

            const note  = must<HTMLElement>(content, '#store-note');
            const error = must<HTMLElement>(content, '#store-error');

            note.textContent  = '';
            error.textContent = '';

            busy = true;

            try
            {
                current = await whileSaving(content, note, () => api.certificates.reload());
                busy    = false;
                draw();
                must<HTMLElement>(content, '#store-note').textContent =
                    `The directory was read again: ${everything().length} certificate(s).`;
            }
            catch (problem)
            {
                busy = false;
                draw();
                must<HTMLElement>(content, '#store-error').textContent = errorMessage(problem);
            }

        }


        async function load(): Promise<void> {

            try
            {
                const loaded = await api.certificates.get();

                if (!cancelled) {
                    current = loaded;
                    draw();
                }
            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The certificate store could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        // A file chosen, or a name typed, for an import not yet made is a
        // draft like any other page's: leaving asks first.
        const release = unsaved.heldBy(() => Array.from(content.querySelectorAll<HTMLFormElement>('form')).
                                                  some(form => typedSinceDrawn(form)));

        void load();

        return () => { cancelled = true; release(); };

    }

};


/**
 * One file's bytes, base64-encoded.
 *
 * Through a data: URL rather than by walking the bytes, because the browser's
 * own encoder is the one that will not get a 3-megabyte string wrong. The
 * prefix up to the comma is the media type the reader chose and is dropped.
 */
function base64Of(file: File): Promise<string> {

    return new Promise((resolve, reject) => {

        const reader = new FileReader();

        reader.onerror = () => reject(new Error(`'${file.name}' could not be read.`));

        reader.onload  = () => {

            const asURL = String(reader.result ?? '');
            const comma = asURL.indexOf(',');

            if (comma < 0) {
                reject(new Error(`'${file.name}' could not be read.`));
                return;
            }

            resolve(asURL.slice(comma + 1));

        };

        reader.readAsDataURL(file);

    });

}
