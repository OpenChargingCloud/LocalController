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

    if (path === '/configuration/csms/keypins' && method === 'PUT') {
        const update = body as { serverKeyPins: string; trustOnFirstUse: 'None' | 'Key'; serverKeyPinProbationTime: number };
        if (update.serverKeyPins.includes('nonsense'))
            return refused(400, "'nonsense' is no key pin");
        held = { ...held, keyPins: { ...held.keyPins!,
                                     serverKeyPins:              update.serverKeyPins.split(',').map(pin => pin.trim()).filter(pin => pin.length > 0),
                                     trustOnFirstUse:            update.trustOnFirstUse,
                                     serverKeyPinProbationTime:  update.serverKeyPinProbationTime,
                                     keyPinned:                  true } };
        return held;
    }

    if (path === '/configuration/csms/pinSetSigners' && method === 'POST') {
        const signer = body as { publicKey: string; description: string };
        held = { ...held, keyPins: { ...held.keyPins!, pinSetSigners: [ ...held.keyPins!.pinSetSigners, { ...signer, createdAt: '2026-11-13T00:00:00Z' } ] } };
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
        state:       { connected: false, connectedSince: null, lastProblem: null, hasCredentials: false, waitingForARestart: [] },
        keyPins:     { serverKeyPins: [], serverKeyPinsVersion: 0, serverKeyPinState: 'Learned', rawPublicKeys: 'Off', trustOnFirstUse: 'Key',
                       serverKeyPinProbationTime: 600, pinSetSigners: [], serverKeyPinLearned: 'ab'.repeat(32), serverKeyPinLearnedAt: '2026-11-13T00:00:00Z',
                       keyPinned: true, overTLS: true, onProbation: true, keyPinShown: 'ab'.repeat(32) }
    };
    refuseConnection = false;
    return open(csmsPage, '/configuration/csms', [ 'csms:read', 'csms:edit', 'certificates:read' ],
                controller, root => root.querySelector('#credentials-form') !== null);
}


describe('the CSMS page', () => {

    it('says how the line believes its CSMS: key-pinned, on probation, by the key pin it learned, and that no signer is believed', async () => {

        const root = await opened();

        assert.match(root.querySelector('#keypins-state')?.textContent ?? '',   /key-pinned/);
        assert.match(root.querySelector('#keypins-state')?.textContent ?? '',   /on probation/);
        assert.match(root.querySelector('#keypins-learned')?.textContent ?? '', new RegExp('ab'.repeat(32)));
        assert.ok(root.querySelector('#keypins-no-signer') !== null, 'no word that a pin set would be refused');

    });

    it('saves the key pins as typed, and shows them as the controller took them', async () => {

        const root = await opened();
        const pin  = 'cd'.repeat(32);

        field(root, '#keypins-form', 'serverKeyPins').value             = pin;
        field(root, '#keypins-form', 'serverKeyPinProbationTime').value = '120';

        submit(root, '#keypins-form');
        await until(() => asked.some(one => one.path === '/configuration/csms/keypins'), 'the key pins were not saved');
        await until(() => (root.querySelector('#keypins-pins')?.textContent ?? '').includes(pin), 'the key pins taken are not shown');

        const sent = asked.find(one => one.path === '/configuration/csms/keypins')?.body as { serverKeyPins: string; trustOnFirstUse: string; serverKeyPinProbationTime: number };

        assert.equal(sent.serverKeyPins,              pin);
        assert.equal(sent.trustOnFirstUse,            'Key');
        assert.equal(sent.serverKeyPinProbationTime,  120);

    });

    it('keeps key pins the controller refused, and says why', async () => {

        const root = await opened();

        field(root, '#keypins-form', 'serverKeyPins').value = 'nonsense';

        submit(root, '#keypins-form');
        await until(() => (root.querySelector('#keypins-error')?.textContent ?? '').length > 0, 'nothing was said of the refusal');

        assert.match(root.querySelector('#keypins-error')?.textContent ?? '', /no key pin/);
        assert.equal(field(root, '#keypins-form', 'serverKeyPins').value, 'nonsense', 'what was typed went');

    });

    it('adds a signer of pin sets, and lists it', async () => {

        const root = await opened();

        field(root, '#signer-form', 'publicKey').value    = 'BASE64KEY';
        field(root, '#signer-form', 'description').value  = 'The CSMS of the site';

        submit(root, '#signer-form');
        await until(() => root.querySelector('#keypins-signers') !== null, 'the signer is not listed');

        assert.deepEqual(asked.find(one => one.path === '/configuration/csms/pinSetSigners')?.body,
                         { publicKey: 'BASE64KEY', description: 'The CSMS of the site' });
        assert.match(root.querySelector('#keypins-signers')?.textContent ?? '', /The CSMS of the site/);

    });

    it('sends whoever looks for the identity profile 3 signs in with to the Identities, where who the controller is as a client is kept', async () => {

        const root = await opened();

        assert.ok(root.querySelector('a[href$="/configuration/identities"]') !== null, 'the identity is not looked for on the Identities');
        assert.ok(root.querySelector('a[href$="/configuration/certificates"]') === null, 'the identity is looked for among the certificates, which keep no key');

    });

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
