/**
 * The server certificates drawn, in a document of happy-dom, against a
 * stand-in controller: a certificate pasted under one key outlives another
 * key being removed or taking its own in, the remark on the kind of key
 * follows the one chosen, and a request made empties its form.
 */

import { anOCPPServer, asked, change, field, open, submit, until, type Asked } from '../../test/controller.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { ServerCertificate, ServerCertificates } from '../api/client.ts';

const { serverCertificatesPage } = await import('./serverCertificates.ts');


function aKey(id: string): ServerCertificate {
    return { id, algorithm: 'ecdsa-p256', createdAt: '2026-10-01T00:00:00Z', subject: 'CN=lc001.example.org',
             hasCertificate: false, canBePresented: true, inUse: false, warnings: [] };
}

let held: ServerCertificates;

function controller({ method, path }: Asked): unknown {

    if (path === '/configuration/ocpp-server')
        return anOCPPServer();

    if (path === '/configuration/ocpp-server/certificates' && method === 'GET')
        return held;

    if (path === '/configuration/ocpp-server/certificates' && method === 'POST') {
        held = { ...held, entries: [ ...held.entries, aKey('k3') ] };
        return { id: 'k3', csr: '-----BEGIN CERTIFICATE REQUEST-----' };
    }

    const one = /^\/configuration\/ocpp-server\/certificates\/([^/]+)$/.exec(path);

    if (one && method === 'DELETE') {
        held = { ...held, entries: held.entries.filter(entry => entry.id !== one[1]) };
        return held;
    }

    if (one && method === 'PUT') {
        held = { ...held, entries: held.entries.map(entry => entry.id === one[1] ? { ...entry, hasCertificate: true, inUse: true } : entry) };
        return { id: one[1], warnings: [] };
    }

    return undefined;

}

async function opened(): Promise<HTMLElement> {
    held = { directory: 'certificates', now: '2026-10-04T12:00:00Z', servedId: null, canImportPrivateKeys: false,
             entries:    [ aKey('k1'), aKey('k2') ],
             algorithms: [ { id: 'ecdsa-p256', name: 'ECDSA P-256', remark: 'What every station takes.' },
                           { id: 'rsa-4096',   name: 'RSA 4096',    remark: 'Takes a while to make.' } ] } as ServerCertificates;
    return open(serverCertificatesPage, '/configuration/ocpp-server/certificates', [ 'certificates:read', 'certificates:edit' ],
                controller, root => root.querySelector('.upload-form[data-id="k2"]') !== null);
}

const pemOf = (root: HTMLElement, id: string) => field<HTMLTextAreaElement>(root, `.upload-form[data-id="${id}"]`, 'pem');


describe('the server certificates', () => {

    it('keep a certificate pasted under one key, and its focus, while another key is removed', async () => {

        const root = await opened();
        const pem  = pemOf(root, 'k2');

        pem.value = '-----BEGIN CERTIFICATE-----\nk2';
        pem.focus();

        root.querySelector<HTMLButtonElement>('.remove[data-id="k1"]')!.click();
        await until(() => root.querySelector('.upload-form[data-id="k1"]') === null, 'the key removed is still drawn');

        assert.ok(pemOf(root, 'k2') === pem, 'the field was made anew');
        assert.equal(pem.value,              '-----BEGIN CERTIFICATE-----\nk2');
        assert.ok(document.activeElement === pem, 'the focus went');

    });

    it('empty the form of the key that took its certificate in, and only that one', async () => {

        const root = await opened();

        pemOf(root, 'k1').value = '-----BEGIN CERTIFICATE-----\nk1';
        pemOf(root, 'k2').value = '-----BEGIN CERTIFICATE-----\nk2';

        submit(root, '.upload-form[data-id="k1"]');
        await until(() => root.querySelector('.entry.in-use') !== null, 'the certificate taken in was not drawn');

        assert.deepEqual(asked.find(one => one.method === 'PUT')?.body, { pem: '-----BEGIN CERTIFICATE-----\nk1' });
        assert.equal(pemOf(root, 'k1').value, '',                                 'what was taken in is still in its form');
        assert.equal(pemOf(root, 'k2').value, '-----BEGIN CERTIFICATE-----\nk2', 'what was pasted under the other key went');

    });

    it('say the remark on the kind of key chosen, after a draw too', async () => {

        const root   = await opened();
        const remark = () => root.querySelector('#algorithm-remark')!.textContent!.trim();

        assert.equal(remark(), 'What every station takes.');

        change(field<HTMLSelectElement>(root, '#create-form', 'algorithm'), 'rsa-4096');
        assert.equal(remark(), 'Takes a while to make.');

        root.querySelector<HTMLButtonElement>('.remove[data-id="k1"]')!.click();
        await until(() => root.querySelector('.upload-form[data-id="k1"]') === null, 'the key removed is still drawn');

        assert.equal(field<HTMLSelectElement>(root, '#create-form', 'algorithm').value, 'rsa-4096');
        assert.equal(remark(), 'Takes a while to make.');

    });

    it('put the request\'s form back to what it starts with once a request is made', async () => {

        const root = await opened();

        field(root, '#create-form', 'subject').value = 'lc002.example.org';
        change(field<HTMLSelectElement>(root, '#create-form', 'algorithm'), 'rsa-4096');

        submit(root, '#create-form');
        await until(() => root.querySelector('.upload-form[data-id="k3"]') !== null, 'the key made was not drawn');

        assert.deepEqual(asked.find(one => one.method === 'POST')?.body, { subject: 'lc002.example.org', algorithm: 'rsa-4096' });
        assert.equal(field(root, '#create-form', 'subject').value,                        'lc001.example.org');
        assert.equal(field<HTMLSelectElement>(root, '#create-form', 'algorithm').value,   'ecdsa-p256');
        assert.equal(root.querySelector('#algorithm-remark')!.textContent!.trim(),        'What every station takes.');
        assert.match(root.querySelector('pre.pem')?.textContent ?? '', /BEGIN CERTIFICATE REQUEST/);

    });

});
