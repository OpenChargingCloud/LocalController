import { api, type ControllerState } from '../api/client';
import type { Page } from '@node/router';
import { reloadButton, shell } from '@node/shell';
import { errorMessage, formatNumber, formatSince, formatTimestamp } from '@node/ui';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * What is going on, apart from how this local controller is configured:
 * whether the line up to the CSMS stands and since when, which charging
 * stations were seen when, and the grid operator's limits in force.
 *
 * Read-only, and for "state:read" - which the support has and which reads
 * none of the configuration (white paper "Signed OCPP Messages and Signer
 * Roles", section 5.3). So nothing here says where the line goes, whom the
 * station port lets in or what anybody signs in with: what the controller
 * sends for this page does not have it, and neither does the page.
 */
export const statePage: Page = {

    title: 'State',

    render({ root }) {

        const content = shell(root, {
            active:    '/state',
            title:     'State',
            subtitle:  'What is going on right now.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        let cancelled = false;

        async function load(): Promise<void> {

            try
            {

                const state = await api.state();

                if (cancelled)
                    return;

                render(content, html`
                    <div class="cards">
                        ${csmsCard(state)}
                        ${stationsCard(state)}
                        ${limitsCard(state)}
                    </div>
                `);

            }
            catch (problem)
            {

                if (cancelled)
                    return;

                render(content, html`
                    <div class="error-box">The state could not be loaded: ${errorMessage(problem)}</div>
                `);

            }

        }

        void load();

        return () => { cancelled = true; };

    }

};


function csmsCard(state: ControllerState): TemplateResult {

    return html`
        <section class="card" id="state-csms">

            <h2><i class="fa-solid fa-satellite-dish"></i> CSMS</h2>

            <dl class="kv">

                <dt>Line</dt>
                <dd>
                    ${!state.csms.enabled
                          ? html`<span class="badge">switched off</span>`
                          : state.csms.connected
                                ? html`<span class="badge ok">connected</span>`
                                : html`<span class="badge warn">not connected</span>`}
                </dd>

                ${state.csms.connectedSince ? html`
                    <dt>Since</dt>
                    <dd>${formatTimestamp(state.csms.connectedSince)}</dd>
                ` : nothing}

            </dl>

        </section>
    `;

}


function stationsCard(state: ControllerState): TemplateResult {

    return html`
        <section class="card" id="state-stations">

            <h2><i class="fa-solid fa-charging-station"></i> Charging stations</h2>

            <dl class="kv">
                <dt>Connected</dt>
                <dd>${state.stations.connected}</dd>
            </dl>

            ${state.stations.seen.length === 0
                  ? html`<p class="muted">No charging station was seen since this controller started.</p>`
                  : html`
                      <div class="table-scroll">
                          <table class="table">
                              <thead>
                                  <tr><th>Station</th><th>Connected</th><th>Since</th><th>Last seen</th></tr>
                              </thead>
                              <tbody>
                                  ${repeat(state.stations.seen, station => station.id, station => html`
                                      <tr data-station=${station.id}>
                                          <td>${station.id}</td>
                                          <td>${station.connected
                                                    ? html`<span class="badge ok">${station.over ?? 'connected'}</span>`
                                                    : html`<span class="badge">not connected</span>`}</td>
                                          <td>${station.connectedSince ? formatTimestamp(station.connectedSince) : '-'}</td>
                                          <td title=${formatTimestamp(station.lastSeen)}>${formatSince(station.lastSeen)}</td>
                                      </tr>
                                  `)}
                              </tbody>
                          </table>
                      </div>
                  `}

        </section>
    `;

}


function limitsCard(state: ControllerState): TemplateResult {

    const active = state.openADR.limits.filter(limit => limit.state === 'active');

    return html`
        <section class="card" id="state-limits">

            <h2><i class="fa-solid fa-bolt"></i> Grid limits</h2>

            <dl class="kv">

                <dt>VTN</dt>
                <dd>
                    ${!state.openADR.running
                          ? html`<span class="badge">not asked</span>`
                          : state.openADR.signedIn
                                ? html`<span class="badge ok">signed in</span>`
                                : html`<span class="badge warn">not signed in</span>`}
                </dd>

                ${state.openADR.lastRead ? html`
                    <dt>Last read</dt>
                    <dd>${formatTimestamp(state.openADR.lastRead)}</dd>
                ` : nothing}

                <dt>Draw at most</dt>
                <dd>${state.openADR.importKW === null ? 'no limit' : `${formatNumber(state.openADR.importKW)} kW`}</dd>

                <dt>Feed in at most</dt>
                <dd>${state.openADR.exportKW === null ? 'no limit' : `${formatNumber(state.openADR.exportKW)} kW`}</dd>

            </dl>

            ${active.length === 0 ? nothing : html`
                <ul>
                    ${repeat(active, limit => limit.eventID, limit => html`
                        <li>${limit.name ?? limit.eventID}: ${limit.kind}, ${formatNumber(limit.kW)} kW
                            ${limit.end ? html`until ${formatTimestamp(limit.end)}` : 'until withdrawn'}</li>
                    `)}
                </ul>
            `}

        </section>
    `;

}
