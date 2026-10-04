import { api, type OCPPServerConfiguration, type ServerCertificate, type ServerCertificates } from '../api/client';
import { auth } from '../auth';
import { must } from '@node/html';
import { toURL } from '@node/basePath';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, field, formatTimestamp } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

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
 *
 * Drawn by view.ts: a draw changes only what differs, so that a certificate
 * pasted under one key outlives another key being made or removed.
 */
export const serverCertificatesPage: Page = {

    title: 'Server certificates',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration/ocpp-server/certificates',
            title:     'Server certificates',
            subtitle:  'What this local controller presents to the charging stations, and what takes over when it runs out.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayManage = auth.can('certificates', 'edit');

        let cancelled = false;
        let store:  ServerCertificates      | null = null;
        let server: OCPPServerConfiguration | null = null;

        /** The request that was just made, offered for download straight away. */
        let justMade: { id: string; csr: string } | null = null;

        /**
         * The kind of key chosen, whose remark the form shows - or undefined
         * while it is the one drawn as chosen.
         */
        let algorithmChosen: string | undefined;


        function draw(): void {

            if (store === null || server === null)
                return;

            const certificates   = store;
            const configuration  = server;
            const kindOfKey      = algorithmChosen ?? 'ecdsa-p256';

            render(content, html`

                ${mayManage ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at the certificates', 'make or replace them')}
                    </div>
                `}

                ${configuration.reachableAs.length > 0 ? nothing : html`
                    <div class="notice warn">
                        This local controller has not been told what it is
                        <a href="${toURL('/configuration/ocpp-server')}">reachable as</a>. A signing request without those names
                        produces a certificate that no charging station will accept, so none can be made yet.
                    </div>
                `}

                <div class="cards">

                    <section class="card">

                        <h2><i class="fa-solid fa-key"></i> Make a signing request</h2>

                        <form id="create-form" class="form-stack" @submit=${create}>

                            <label>Subject
                                <input type="text" name="subject" maxlength="200"
                                       value="${configuration.reachableAs[0] ?? ''}"
                                       placeholder="lc001.example.org"
                                       ?disabled=${!mayManage} />
                            </label>

                            <label>Key
                                <select name="algorithm" id="algorithm" ?disabled=${!mayManage}
                                        @change=${(event: Event) => { algorithmChosen = (event.target as HTMLSelectElement).value; draw(); }}>
                                    ${certificates.algorithms.map(algorithm => html`
                                        <option value="${algorithm.id}"
                                                ?selected=${algorithm.id === 'ecdsa-p256'}>
                                            ${algorithm.name}${algorithm.presentable === false ? ' - not servable here' : ''}
                                        </option>
                                    `)}
                                </select>
                            </label>

                            <span class="hint" id="algorithm-remark">
                                ${certificates.algorithms.find(one => one.id === kindOfKey)?.remark ?? ''}
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
                                        ?disabled=${!mayManage || configuration.reachableAs.length === 0}>
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

                        ${justMade === null ? nothing : html`
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
                              : repeat(certificates.entries, entry => entry.id, entry => entryCard(entry))}

                    </section>

                </div>
            `);

        }


        function entryCard(entry: ServerCertificate): TemplateResult {

            const certificate = entry.certificate;

            return html`
                <div class="entry ${entry.inUse ? 'in-use' : ''}">

                    <div class="entry-head">
                        <div>
                            <code>${entry.id}</code>
                            <span class="badge">${entry.algorithm}</span>
                            ${entry.inUse ? html`<span class="badge ok">being presented</span>` : nothing}
                            ${certificate ? html`<span class="badge ${stateClass(certificate.state)}">${certificate.state}</span>` : nothing}
                            ${certificate ? nothing : html`<span class="badge warn">waiting for a certificate</span>`}
                            ${entry.canBePresented ? nothing : html`<span class="badge warn">not servable here</span>`}
                        </div>
                        <div class="entry-actions">
                            <a class="btn small" href="${api.ocppServer.certificates.csrURL(entry.id)}" download>
                                Signing request
                            </a>
                            <button type="button" class="btn small danger remove" data-id="${entry.id}"
                                    ?disabled=${!mayManage} @click=${() => void remove(entry.id)}>
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
                        ` : nothing}
                    </dl>

                    ${entry.warnings.length === 0 ? nothing : html`
                        <div class="notice warn">
                            <ul>${entry.warnings.map(warning => html`<li>${warning}</li>`)}</ul>
                        </div>
                    `}

                    <form class="form-stack upload-form" data-id="${entry.id}" @submit=${(event: SubmitEvent) => void upload(entry.id, event)}>

                        <label>${certificate ? 'Replace this certificate' : 'The certificate that answers this request'}
                            <textarea name="pem" rows="5" placeholder="-----BEGIN CERTIFICATE-----&#10;..."
                                      ?disabled=${!mayManage}></textarea>
                        </label>

                        <span class="hint">
                            The certificate first, then the intermediates that lead to it. The root is not needed:
                            a charging station either already trusts it or does not, and sending it changes
                            neither. A certificate that is already expired, or that belongs to a key that is not
                            here, is refused now rather than at the hour it would have taken over.
                        </span>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayManage}>Take it in</button>
                            <span class="form-error upload-error" role="alert"></span>
                        </div>

                    </form>

                </div>
            `;

        }


        function create(event: SubmitEvent): void {

            event.preventDefault();

            const form = event.currentTarget as HTMLFormElement;

            void makeKey(form, field(form, 'subject'), field(form, 'algorithm'));

        }


        async function makeKey(form: HTMLFormElement, subject: string, algorithm: string): Promise<void> {

            const error = must<HTMLElement>(content, '#create-error');
            error.textContent = '';

            try
            {

                const made = await api.ocppServer.certificates.create(subject, algorithm);

                if (cancelled)
                    return;

                justMade         = made;
                store            = await api.ocppServer.certificates.get();
                algorithmChosen  = undefined;

                draw();

                // A draw leaves a form as it is typed into; this one was made
                // into a request, so it goes back to what it starts with.
                form.reset();

            }
            catch (problem)
            {
                if (!cancelled)
                    error.textContent = errorMessage(problem);
            }

        }


        async function upload(id: string, event: SubmitEvent): Promise<void> {

            event.preventDefault();

            const form   = event.currentTarget as HTMLFormElement;
            const pem    = field(form, 'pem', false);
            const error  = form.querySelector<HTMLElement>('.upload-error');

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

                // Taken in: this key's form is emptied, and every other one
                // keeps what is pasted into it.
                form.reset();

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

                store            = certificates;
                server           = configuration;
                algorithmChosen  = undefined;

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
        const release = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        return () => { cancelled = true; release(); };

    }

};


function stateClass(state: string): string {
    return state === 'valid' ? 'ok' : state === 'pending' ? '' : 'warn';
}
