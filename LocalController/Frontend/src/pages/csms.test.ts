/**
 * The CSMS page drawn, in a document of happy-dom, against a stand-in
 * controller: what is typed into one of its two forms - and its focus -
 * outlives the other being saved, a form saved says what the controller took,
 * and one refused keeps what is typed and says why.
 */

import { asked, field, open, refused, said, settled, submit, type, until, type Asked } from '../../test/controller.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { CSMSConfiguration } from '../api/client.ts';

const { csmsPage } = await import('./csms.ts');


let held: CSMSConfiguration;

/** Refuses the connection's settings where told to. */
let refuseConnection = false;

function controller({ method, path, body }: Asked): unknown {

    if (path === '/configuration/certificates')
        return { certificates: { tlsIdentity: [] } };

    if (path === '/configuration/csms' && method === 'PUT') {
        if (refuseConnection)
            return refused(400, 'ws:// is for profile 1 only');
        const update = body as Partial<CSMSConfiguration>;
        // A controller takes what it takes: a ping every half minute at the most often.
        held = { ...held, ...update, pingEvery: Math.max(update.pingEvery ?? held.pingEvery, 30) };
        return held;
    }

    if (path === '/configuration/csms')
        return held;

    if (path === '/configuration/csms/credentials' && method === 'PUT') {
        const said = body as { username: string; password?: string };
        held = { ...held, credentials: { ...held.credentials, username: said.username, hasPassword: said.password !== undefined },
                          state:       { ...held.state, hasCredentials: true } };
        return held;
    }

    return undefined;

}

async function opened(): Promise<HTMLElement> {
    held = {
        enabled: true, url: 'wss://csms.example.org/ocpp/lc001', securityProfile: 2, subprotocols: [ 'ocpp2.1' ],
        checkCertificateRevocation: false, pingEvery: 60, requestTimeout: 30, reconnectInitialDelay: 5, reconnectMaxDelay: 300,
        credentials: { file: 'csms.secret', username: null, hasPassword: false, hasTOTP: false, minPasswordLength: 16 },
        state:       { connected: false, connectedSince: null, lastProblem: null, hasCredentials: false, waitingForARestart: [] }
    };
    refuseConnection = false;
    return open(csmsPage, '/configuration/csms', [ 'csms:read', 'csms:edit', 'certificates:read' ],
                controller, root => root.querySelector('#credentials-form') !== null);
}


describe('the CSMS page', () => {

    it('keeps what is typed into the credentials, and its focus, while the connection is saved', async () => {

        const root      = await opened();
        const username  = field(root, '#credentials-form', 'username');
        const password  = field(root, '#credentials-form', 'password');

        username.value = 'lc001';
        password.value = 'issued by the CSMS, typed here';
        password.focus();

        submit(root, '#connection-form');
        await until(() => asked.some(one => one.method === 'PUT') && root.querySelector('#connection-note')?.textContent === '',
                    'the connection was not saved');

        assert.ok(field(root, '#credentials-form', 'password') === password, 'the field was made anew');
        assert.equal(username.value,          'lc001');
        assert.equal(password.value,          'issued by the CSMS, typed here');
        assert.ok(document.activeElement === password, 'the focus went');

    });

    it('shows the connection as the controller took it once it is saved, with nothing left to save', async () => {

        const root = await opened();
        const ping = field(root, '#connection-form', 'pingEvery');

        ping.value = '7';

        submit(root, '#connection-form');
        await until(() => held.pingEvery === 30 && root.querySelector('#connection-note')?.textContent === '', 'the connection was not saved');

        assert.equal((asked.find(one => one.method === 'PUT')?.body as { pingEvery: number }).pingEvery, 7);
        assert.equal(field(root, '#connection-form', 'pingEvery').value,         '30', 'the field says what was typed, not what the controller took');
        assert.equal(field(root, '#connection-form', 'pingEvery').defaultValue,  '30');

    });

    it('keeps what is typed into a connection the controller refused, and says why', async () => {

        const root = await opened();
        const url  = field(root, '#connection-form', 'url');

        refuseConnection = true;
        url.value = 'ws://csms.example.org/ocpp/lc001';

        submit(root, '#connection-form');
        await until(() => root.querySelector('#connection-error')?.textContent !== '', 'the refusal was not said');

        // And drawn again from what the controller has, which it is not.
        await until(() => asked.filter(one => one.method === 'GET' && one.path === '/configuration/csms').length === 2,
                    'the page did not ask what the controller has after the refusal');
        await settled();

        assert.equal(root.querySelector('#connection-error')!.textContent, 'ws:// is for profile 1 only');
        assert.equal(field(root, '#connection-form', 'url').value, 'ws://csms.example.org/ocpp/lc001', 'what was typed went');

    });

    it('empties the secret and names who it signs in as, once the credentials are saved', async () => {

        const root = await opened();

        field(root, '#credentials-form', 'username').value = 'lc001';
        field(root, '#credentials-form', 'password').value = 'issued by the CSMS, typed here';

        submit(root, '#credentials-form');
        await until(() => held.credentials.username === 'lc001' && root.textContent!.includes('Set for'), 'the credentials were not saved');

        assert.equal(field(root, '#credentials-form', 'password').value,          '', 'the secret saved is still in the form');
        assert.equal(field(root, '#credentials-form', 'username').value,          'lc001');
        assert.equal(field(root, '#credentials-form', 'username').defaultValue,   'lc001');

    });

});


describe('the Reload of the CSMS page', () => {

    // Whether it asks where nothing is typed is not asked here: happy-dom
    // takes the security profile's choice for one somebody made, as it keeps
    // no defaultSelected. In Chrome it asks nothing then.
    it('asks before it throws away what is typed, and then asks the controller again', async () => {

        const root = await opened();

        type(field(root, '#credentials-form', 'username'), 'lc001');

        const before = asked.length;

        root.querySelector<HTMLButtonElement>('.page-actions #reload')!.click();

        await until(() => asked.slice(before).some(one => one.method === 'GET' && one.path === '/configuration/csms'),
                    'the controller was not asked again');

        assert.equal(said.length, 1, `asked ${said.length} times before the typed username was thrown away`);
        assert.match(said[0]!, /not been told about/);

    });

});
