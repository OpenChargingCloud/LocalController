/*
 * A local controller that is a stand-in for fetch, and a document of happy-dom
 * to draw a page into: what the pages' tests in src/pages/*.test.ts share.
 *
 * Imported first, before a page: lit-html looks for the document as it is
 * loaded, and the pages load it.
 *
 *   import { open, ... } from '../../test/controller.ts';
 *   const { csmsPage } = await import('./csms.ts');
 */

import '@node/../test/dom.ts';

import { strict as assert } from 'node:assert';

import type { Page } from '@node/router';
import type { OCPPServerConfiguration } from '../src/api/client.ts';

const { auth }            = await import('../src/auth.ts');
const { configureShell }  = await import('@node/shell.ts');


/** What the stand-in was asked: GET /configuration/csms, with what came along. */
export interface Asked {
    method:  string;
    path:    string;
    body:    unknown;
}

/**
 * How the stand-in answers: with a value, sent as JSON with 200; with a
 * Response as it is; with undefined, as nothing it knows - 404.
 */
export type Answers = (asked: Asked) => unknown;

/** Everything asked since the page was opened, in order. */
export const asked: Asked[] = [];

let answers: Answers = () => undefined;

globalThis.fetch = (async (input: string | URL | Request, init?: RequestInit) => {

    const path   = new URL(String(input), 'http://127.0.0.1/').pathname.replace(/^\/api\/v1/, '');
    const method = init?.method ?? 'GET';
    const body   = init?.body === undefined ? undefined : JSON.parse(String(init.body)) as unknown;
    const one    = { method, path, body };

    asked.push(one);

    const answer = answers(one);

    if (answer instanceof Response)
        return answer;

    if (answer === undefined)
        return refused(404, `nothing at ${method} ${path}`);

    return new Response(JSON.stringify(answer), { status: 200, headers: { 'Content-Type': 'application/json' } });

}) as typeof fetch;

/** An answer that refuses, with what the controller says why. */
export function refused(status: number, error: string): Response {
    return new Response(JSON.stringify({ error }), { status, headers: { 'Content-Type': 'application/json' } });
}

/** What window.alert and window.confirm were asked; confirm says yes. */
export const said: string[] = [];

window.alert   = (text?: unknown) => { said.push(String(text)); };
window.confirm = (text?: unknown) => { said.push(String(text)); return true; };


const wait = (ms = 0) => new Promise(resolve => setTimeout(resolve, ms));

/** Wait until it holds, a second at the most, and fail with what was said if it does not. */
export async function until(what: () => boolean, failure: string): Promise<void> {
    for (let i = 0; i < 200 && !what(); i++)
        await wait(5);
    assert.ok(what(), failure);
}

/** Let what was asked be answered and drawn, where there is nothing to wait for by name. */
export async function settled(): Promise<void> {
    for (let i = 0; i < 10; i++)
        await wait(2);
}

/**
 * A page drawn into a document of its own, for somebody with these
 * permissions, against a stand-in that answers so.
 */
export async function open(page:         Page,
                           path:         string,
                           permissions:  string[],
                           how:          Answers,
                           drawn:        (root: HTMLElement) => boolean): Promise<HTMLElement> {

    asked.length = 0;
    said.length  = 0;
    answers      = how;

    configureShell({ name: 'Local Controller', icon: 'fa-sitemap', menu: [] });
    auth.set({ username: 'alice', roles: [ 'admin' ], permissions, mayReadTheLog: false } as never);

    const root = document.createElement('div');
    document.body.replaceChildren(root);

    page.render({ root, url: new URL(`http://127.0.0.1${path}`), params: {}, navigate: () => undefined } as never);

    await until(() => drawn(root), `${path} did not draw`);

    return root;

}

/** The charging station server, as a controller that has just been set up has it. */
export function anOCPPServer(): OCPPServerConfiguration {
    return {
        enabled:                     false,
        address:                     null,
        port:                        8443,
        securityProfiles:            [ 1, 2, 3 ],
        subprotocols:                [ 'ocpp2.1' ],
        reachableAs:                 [ 'lc001.example.org' ],
        minTLSVersion:               '1.2',
        checkCertificateRevocation:  false,
        maxConnections:              100,
        pingEverySeconds:            60,
        logging:                     { connections: true, authentication: true, messages: false, payloads: false,
                                       payloadsUntil: null, payloadsNow: false, pings: false },
        state:                       { running: false, tls: false, url: 'wss://lc001.example.org:8443', connections: 0,
                                       stationLogins: 0, trustedChains: 1, hasCertificate: false, waitingForARestart: [] },
        limits:                      { subprotocols: [ 'ocpp1.6', 'ocpp2.0.1', 'ocpp2.1' ], securityProfiles: [ 1, 2, 3 ],
                                       tlsVersions: [ '1.2', '1.3' ], maxConnections: 1000, maxReachableAs: 8,
                                       suggestedPayloadWindowSeconds: 3600 },
        file:                        'ocppServer.json'
    };
}

/** The field of a form, by the form's selector and the field's name. */
export function field<T extends HTMLElement = HTMLInputElement>(root: HTMLElement, form: string, name: string): T {
    const found = root.querySelector<T>(`${form} [name="${name}"]`);
    assert.ok(found, `${form} has no field ${name}`);
    return found;
}

/** Sent as the browser sends a form once it is valid: happy-dom's checks are not a browser's. */
export function submit(root: HTMLElement, form: string): void {
    const found = root.querySelector<HTMLFormElement>(form);
    assert.ok(found, `there is no ${form}`);
    found.dispatchEvent(new SubmitEvent('submit', { bubbles: true, cancelable: true }));
}

/** A box or chooser changed by hand: its value set, and the change said. */
export function change(element: HTMLInputElement | HTMLSelectElement, to: boolean | string): void {
    if (typeof to === 'boolean')
        (element as HTMLInputElement).checked = to;
    else
        element.value = to;
    element.dispatchEvent(new Event('change', { bubbles: true }));
}
