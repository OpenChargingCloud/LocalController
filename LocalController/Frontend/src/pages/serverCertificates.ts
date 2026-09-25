import { api, type OCPPServerConfiguration, type ServerCertificate, type ServerCertificates } from '../api/client';
import { auth } from '../auth';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, field, formatTimestamp } from '../ui';
import { typedSinceDrawn, unsaved } from '../unsaved';

/**
 * The certificates this local controller presents to the charging stations.
 *
 * The page is built around the one thing that is hard about certificates:
 * replacing one before it runs out, without a window in which nothing works.
 * So more than one lives here at a time, the replacement is uploaded the day it
 * arrives even if it only becomes valid in two days, and the controller
 * switches over by itself at the moment it may.
 *
 * There is no way to upload a private key, and saying so on the page is
 * deliberate: the key is made here and never leaves, and somebody looking for
 * the button should find the reason instead.
 */
export const serverCertificatesPage: Page = {

    title: 'Server certificates',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/ocpp-server/certificates',
            title:     'Server certificates',
            subtitle:  'What this local controller presents to the charging stations, and what takes over when it runs out.',
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

        const mayManage = auth.can('manageCertificates');

        let cancelled = false;
        let store:  ServerCertificates      | null = null;
        let server: OCPPServerConfiguration | null = null;

        /** The request that was just made, offered for download straight away. */
        let justMade: { id: string; csr: string } | null = null;


        function draw(): void {

            if (store === null || server === null)
                return;

            const certificates = store;
            const configuration = server;

            render(content, html`

                ${mayManage ? '' : html`
                    <div class="notice">
                        Signed in as ${auth.user?.roles.join(', ') ?? 'somebody'}, which may look at the
                        certificates but not make or replace them. That needs the system administrator role.
                    </div>
                `}

                ${configuration.reachableAs.length > 0 ? '' : html`
                    <div class="notice warn">
                        This local controller has not been told what it is
                        <a href="/configuration/ocpp-server">reachable as</a>. A signing request without those names
                        produces a certificate that no charging station will accept, so none can be made yet.
                    </div>
                `}

                <div class="cards">

                    <section class="card">

                        <h2><i class="fa-solid fa-key"></i> Make a signing request</h2>

                        <form id="create-form" class="form-stack">

                            <label>Subject
                                <input type="text" name="subject" maxlength="200"
                                       value="${configuration.reachableAs[0] ?? ''}"
                                       placeholder="lc001.example.org"
                                       ${mayManage ? '' : html`disabled`} />
                            </label>

                            <label>Key
                                <select name="algorithm" id="algorithm" ${mayManage ? '' : html`disabled`}>
                                    ${certificates.algorithms.map(algorithm => html`
                                        <option value="${algorithm.id}"
                                                ${algorithm.id === 'ecdsa-p256' ? html`selected` : ''}>
                                            ${algorithm.name}${algorithm.presentable === false ? ' - not servable here' : ''}
                                        </option>
                                    `)}
                                </select>
                            </label>

                            <span class="hint" id="algorithm-remark">
                                ${certificates.algorithms.find(algorithm => algorithm.id === 'ecdsa-p256')?.remark ?? ''}
                            </span>

                            <div class="notice">
                                <strong>Making a key and serving it are two questions.</strong> Every kind here can be
                                generated and made into a signing request. Whether this machine can then hold the
                                certificate up to a charging station depends on its TLS stack - Ed25519, Ed448, P-521
                                and the post-quantum kinds are refused by some platforms and served by others. This
                                local controller finds out by trying, and says so beside the certificate rather than
                                letting a handshake fail.
                            </div>

                            <p class="hint">
                                The request will be made out for
                                ${configuration.reachableAs.length > 0
                                      ? html`<strong>${configuration.reachableAs.join(', ')}</strong>`
                                      : html`<em>nothing</em>`}.
                            </p>

                            <div class="form-actions">
                                <button type="submit" class="btn primary"
                                        ${mayManage && configuration.reachableAs.length > 0 ? '' : html`disabled`}>
                                    Generate a key and a request
                                </button>
                                <span id="create-error" class="form-error" role="alert"></span>
                            </div>

                            <div class="notice">
                                The private key is generated here and never leaves: there is no import, and no
                                route that would hand one out. What leaves is the signing request, which is a
                                public document.
                            </div>

                        </form>

                        ${justMade === null ? '' : html`
                            <div class="notice ok">
                                <strong>The request for '${justMade.id}' is ready.</strong>
                                <a class="btn small" href="${api.ocppServer.certificates.csrURL(justMade.id)}" download>
                                    Download it
                                </a>
                                <pre class="pem">${justMade.csr}</pre>
                            </div>
                        `}

                    </section>

                    <section class="card wide">

                        <h2><i class="fa-solid fa-certificate"></i> Keys and certificates</h2>

                        <p class="hint">
                            In ${certificates.directory}. By this controller's clock it is
                            ${formatTimestamp(certificates.now)} - which is what decides which of these is in its
                            window, so a controller whose clock is wrong presents the wrong one.
                        </p>

                        ${certificates.entries.length === 0
                              ? html`<p class="muted">No key has been made yet.</p>`
                              : certificates.entries.map(entry => entryCard(entry))}

                    </section>

                </div>
            `);

            wire();

        }


        function entryCard(entry: ServerCertificate): HTMLFragment {

            const certificate = entry.certificate;

            return html`
                <div class="entry ${entry.inUse ? 'in-use' : ''}">

                    <div class="entry-head">
                        <div>
                            <code>${entry.id}</code>
                            <span class="badge">${entry.algorithm}</span>
                            ${entry.inUse ? html`<span class="badge ok">being presented</span>` : ''}
                            ${certificate ? html`<span class="badge ${stateClass(certificate.state)}">${certificate.state}</span>` : ''}
                            ${certificate ? '' : html`<span class="badge warn">waiting for a certificate</span>`}
                            ${entry.canBePresented ? '' : html`<span class="badge warn">not servable here</span>`}
                        </div>
                        <div class="entry-actions">
                            <a class="btn small" href="${api.ocppServer.certificates.csrURL(entry.id)}" download>
                                Signing request
                            </a>
                            <button type="button" class="btn small danger remove" data-id="${entry.id}"
                                    ${mayManage ? '' : html`disabled`}>
                                Remove
                            </button>
                        </div>
                    </div>

                    <dl class="kv">
                        <dt>Asked for</dt>
                        <dd>${entry.subject}</dd>

                        <dt>Key made</dt>
                        <dd>${formatTimestamp(entry.createdAt)}</dd>

                        ${certificate ? html`
                            <dt>Issued to</dt>
                            <dd>${certificate.subject}</dd>

                            <dt>Issued by</dt>
                            <dd>${certificate.issuer}</dd>

                            <dt>Valid for</dt>
                            <dd>${certificate.subjectAltNames.length > 0 ? certificate.subjectAltNames.join(', ') : '-'}</dd>

                            <dt>Valid from</dt>
                            <dd>${formatTimestamp(certificate.notBefore)}</dd>

                            <dt>Valid until</dt>
                            <dd>
                                ${formatTimestamp(certificate.notAfter)}
                                <span class="${certificate.daysRemaining < 30 ? 'warn' : 'muted'}">
                                    (${certificate.daysRemaining} day(s))
                                </span>
                            </dd>

                            <dt>Intermediates</dt>
                            <dd>${certificate.intermediates}</dd>

                            <dt>Thumbprint</dt>
                            <dd><code class="small">${certificate.thumbprint}</code></dd>
                        ` : ''}
                    </dl>

                    ${entry.warnings.length === 0 ? '' : html`
                        <div class="notice warn">
                            <ul>${entry.warnings.map(warning => html`<li>${warning}</li>`)}</ul>
                        </div>
                    `}

                    <form class="form-stack upload-form" data-id="${entry.id}">

                        <label>${certificate ? 'Replace this certificate' : 'The certificate that answers this request'}
                            <textarea name="pem" rows="5" placeholder="-----BEGIN CERTIFICATE-----&#10;..."
                                      ${mayManage ? '' : html`disabled`}></textarea>
                        </label>

                        <span class="hint">
                            The certificate first, then the intermediates that lead to it. The root is not needed:
                            a charging station either already trusts it or does not, and sending it changes
                            neither. A certificate that is already expired, or that belongs to a key that is not
                            here, is refused now rather than at the hour it would have taken over.
                        </span>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ${mayManage ? '' : html`disabled`}>Take it in</button>
                            <span class="form-error upload-error" role="alert"></span>
                        </div>

                    </form>

                </div>
            `;

        }


        function wire(): void {

            if (!mayManage)
                return;

            const chooser = content.querySelector<HTMLSelectElement>('#algorithm');
            const remark  = content.querySelector<HTMLElement>('#algorithm-remark');

            chooser?.addEventListener('change', () => {
                if (remark)
                    remark.textContent = store?.algorithms.find(algorithm => algorithm.id === chooser.value)?.remark ?? '';
            });

            must<HTMLFormElement>(content, '#create-form').addEventListener('submit', event => {

                event.preventDefault();

                const form = event.target as HTMLFormElement;

                void create(field(form, 'subject'), field(form, 'algorithm'));

            });

            content.querySelectorAll<HTMLFormElement>('.upload-form').forEach(form => {
                form.addEventListener('submit', event => {
                    event.preventDefault();
                    void upload(form.dataset.id ?? '', field(form, 'pem', false), form);
                });
            });

            content.querySelectorAll<HTMLButtonElement>('.remove').forEach(button => {
                button.addEventListener('click', () => void remove(button.dataset.id ?? ''));
            });

        }


        async function create(subject: string, algorithm: string): Promise<void> {

            const error = must<HTMLElement>(content, '#create-error');
            error.textContent = '';

            try
            {

                const made = await api.ocppServer.certificates.create(subject, algorithm);

                if (cancelled)
                    return;

                justMade = made;
                store    = await api.ocppServer.certificates.get();

                draw();

            }
            catch (problem)
            {
                if (!cancelled)
                    error.textContent = errorMessage(problem);
            }

        }


        async function upload(id: string, pem: string, form: HTMLFormElement): Promise<void> {

            const error = form.querySelector<HTMLElement>('.upload-error');

            if (error)
                error.textContent = '';

            try
            {

                const answer = await api.ocppServer.certificates.upload(id, pem);

                if (cancelled)
                    return;

                justMade = null;
                store    = await api.ocppServer.certificates.get();
                server   = await api.ocppServer.get();

                draw();

                if (answer.warnings.length > 0)
                    window.alert(`The certificate was taken in, with something to say about it:\n\n${answer.warnings.join('\n\n')}`);

            }
            catch (problem)
            {
                if (error && !cancelled)
                    error.textContent = errorMessage(problem);
            }

        }


        async function remove(id: string): Promise<void> {

            if (!window.confirm(`Remove the key '${id}' and everything belonging to it? This cannot be undone.`))
                return;

            try
            {

                store = await api.ocppServer.certificates.remove(id);

                if (cancelled)
                    return;

                justMade = null;
                server   = await api.ocppServer.get();

                draw();

            }
            catch (problem)
            {
                if (!cancelled)
                    window.alert(errorMessage(problem));
            }

        }


        async function load(): Promise<void> {

            render(content, html`<div class="loading">Loading ...</div>`);

            try
            {

                const [certificates, configuration] = await Promise.all([
                    api.ocppServer.certificates.get(),
                    api.ocppServer.get()
                ]);

                if (cancelled)
                    return;

                store  = certificates;
                server = configuration;

                draw();

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`<div class="error-box">${errorMessage(problem)}</div>`);
            }

        }


        // A half-typed subject, or a certificate pasted but not yet taken in,
        // is work like any other.
        const release = unsaved.heldBy(() => Array.from(content.querySelectorAll<HTMLFormElement>('form')).
                                                   some(form => typedSinceDrawn(form)));

        void load();

        return () => { cancelled = true; release(); };

    }

};


function stateClass(state: string): string {
    return state === 'valid' ? 'ok' : state === 'pending' ? '' : 'warn';
}
