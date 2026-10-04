import { api } from '../api/client';
import { card, librariesCard } from '@node/cards';
import { html as stringHTML, must } from '@node/html';
import type { Page } from '@node/router';
import { shell } from '@node/shell';
import { errorMessage, formatSince, formatValue } from '@node/ui';
import { html, render } from '@node/view';

/**
 * What this local controller is made of - read-only: it answers "what am I
 * running", not "change it". What can be changed has a page of its own, and
 * both are reachable from the same menu.
 *
 * The fields of each section are rendered from whatever the controller sends
 * rather than from a list kept here, so a field added on the server shows up
 * without a change to this page. The sections are not: which of them there
 * are, their order and their headings are decided here, and a section the
 * server adds shows up once it has a card below.
 */
export const configurationPage: Page = {

    title: 'Configuration',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration',
            title:     'Configuration',
            subtitle:  'What this local controller is made of.',
            actions:   stringHTML`<button type="button" id="reload" class="btn small">Reload</button>`
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

                        ${card('Local controller', 'fa-sitemap', configuration.controller, stringHTML`
                            <div class="kv">
                                <span class="k">Uptime</span>
                                <span class="v">${status.uptime} <span class="muted">(started ${formatSince(status.startedAt)})</span></span>
                            </div>
                        `)}

                        ${card('HTTP server',   'fa-server',    configuration.http)}
                        ${card('Accounts',      'fa-user-lock', configuration.web)}
                        ${card('Event log',     'fa-list-ul',   configuration.log)}
                        ${card('Time',          'fa-clock',     configuration.time)}

                        ${card(
                            `OCPP ${formatValue(configuration.ocpp.version)} - ${formatValue(configuration.ocpp.role)}`,
                            'fa-plug',
                            configuration.ocpp
                        )}

                        ${librariesCard(configuration.assemblies)}

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
