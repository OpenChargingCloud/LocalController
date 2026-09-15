import { api, type Configuration } from '../api/client';
import { html, must, render, type HTMLFragment } from '../html';
import type { Page } from '../router';
import { shell } from '../shell';
import { errorMessage, formatSince, formatValue, humanizeKey } from '../ui';

/**
 * What this local controller is made of - read-only: it answers "what am I
 * running", not "change it". What can be changed has a page of its own, and
 * both are reachable from the same menu.
 *
 * The sections are rendered from whatever the controller sends rather than
 * from a list kept here, so a field added on the server shows up without a
 * change to this page. Only the order and the headings are decided here.
 */
export const configurationPage: Page = {

    title: 'Configuration',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration',
            title:     'Configuration',
            subtitle:  'What this local controller is made of.',
            actions:   html`<button type="button" id="reload" class="btn small">Reload</button>`
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        must<HTMLButtonElement>(root, '#reload').
            addEventListener('click', () => void load());

        let cancelled = false;

        async function load(): Promise<void> {

            try
            {

                const [configuration, status] = await Promise.all([
                    api.configuration(),
                    api.status()
                ]);

                if (cancelled)
                    return;

                render(content, html`

                    <div class="cards">

                        ${card('Local controller', 'fa-sitemap', configuration.controller, html`
                            <div class="kv">
                                <span class="k">Uptime</span>
                                <span class="v">${status.uptime} <span class="muted">(started ${formatSince(status.startedAt)})</span></span>
                            </div>
                        `)}

                        ${card('HTTP server',   'fa-server',    configuration.http)}
                        ${card('Web login',     'fa-user-lock', configuration.web)}
                        ${card('Event log',     'fa-list-ul',   configuration.log)}
                        ${card('Time',          'fa-clock',     configuration.time)}

                        ${card(
                            `OCPP ${formatValue(configuration.ocpp.version)} - ${formatValue(configuration.ocpp.role)}`,
                            'fa-plug',
                            configuration.ocpp
                        )}

                        <section class="card">
                            <h2><i class="fa-solid fa-cubes"></i> Libraries</h2>
                            <div class="kv-list">
                                ${configuration.assemblies.map(assembly => html`
                                    <div class="kv">
                                        <span class="k">${formatValue(assembly.name)}</span>
                                        <span class="v">
                                            ${formatValue(assembly.version)}
                                            <span class="muted small">${formatValue(assembly.assembly)}</span>
                                        </span>
                                    </div>
                                `)}
                            </div>
                        </section>

                    </div>

                `);

            }
            catch (problem)
            {

                if (cancelled)
                    return;

                render(content, html`
                    <div class="error-box">The configuration could not be loaded: ${errorMessage(problem)}</div>
                `);

            }

        }

        void load();

        return () => { cancelled = true; };

    }

};


/**
 * One section: every field the controller sent, in the order it sent them,
 * with anything that is itself a list of things rendered as a nested block.
 */
function card(title:    string,
              icon:     string,
              values:   Record<string, unknown>,
              extra?:   HTMLFragment): HTMLFragment {

    const entries = Object.entries(values ?? {});

    return html`
        <section class="card">

            <h2><i class="fa-solid ${icon}"></i> ${title}</h2>

            <div class="kv-list">

                ${extra ?? ''}

                ${entries.map(([key, value]) => Array.isArray(value) && value.some(item => typeof item === 'object' && item !== null)
                    ? html`
                        <div class="kv-nested">
                            <span class="k">${humanizeKey(key)}</span>
                            <div class="nested">
                                ${(value as Record<string, unknown>[]).map(item => html`
                                    <div class="nested-item">
                                        ${Object.entries(item).map(([itemKey, itemValue]) => html`
                                            <div class="kv">
                                                <span class="k">${humanizeKey(itemKey)}</span>
                                                <span class="v">${formatValue(itemValue)}</span>
                                            </div>
                                        `)}
                                    </div>
                                `)}
                            </div>
                        </div>
                    `
                    : html`
                        <div class="kv">
                            <span class="k">${humanizeKey(key)}</span>
                            <span class="v">${formatValue(value)}</span>
                        </div>
                    `
                )}

            </div>

        </section>
    `;

}
