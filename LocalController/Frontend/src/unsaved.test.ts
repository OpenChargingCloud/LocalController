/**
 * What the web interface does with work the local controller has not been told about.
 *
 * Run with `npm test`, which is Node's own runner reading the TypeScript as it
 * stands - no bundler, no browser, no dependency that is not already here.
 *
 * What is pinned is what was measured to be wrong: an EVSE added to the list
 * and then thrown away by one click on Reload, and the same work thrown away
 * by one click on a menu entry - both without a word.
 */

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import { theQuestion, typedSinceDrawn, unsaved } from './unsaved.ts';


/** What was asked, and what the answer was. */
let asked: string[] = [];

const answering = (How: boolean | 'cannot') => {
    asked = [];
    (globalThis as unknown as { confirm: unknown }).confirm = (question: string) => {
        asked.push(question);
        if (How === 'cannot')
            throw new Error('no dialogs here');
        return How;
    };
};

/** A page holding whatever it is told to hold. */
const aPageHolding = (Something: boolean) => unsaved.heldBy(() => Something);


describe('a page holding nothing', () => {

    it('is not asked about, because there is nothing to ask', () => {

        answering(false);

        const release = aPageHolding(false);

        assert.equal(unsaved.any(),       false);
        assert.equal(unsaved.mayBeLost(), true);
        assert.deepEqual(asked, [], 'somebody was asked about nothing');

        release();

    });

});


describe('a page holding something', () => {

    it('is asked about before it is left', () => {

        answering(true);

        const release = aPageHolding(true);

        assert.equal(unsaved.any(), true);
        assert.equal(unsaved.mayBeLost(), true);
        assert.equal(asked.length, 1);
        assert.match(asked[0]!, /not been told/);

        release();

    });

    it('stays put when the answer is no', () => {

        answering(false);

        const release = aPageHolding(true);

        assert.equal(unsaved.mayBeLost(), false, 'the work was thrown away anyway');

        release();

    });

    it('says what will happen to the work, not just that something is wrong', () => {

        assert.match(theQuestion, /changes/);
        assert.match(theQuestion, /Leave them behind\?/);

    });

    it('gives way where the question cannot be put at all', () => {

        // Of the two ways to be wrong, trapping somebody on a page they want
        // to leave is the worse one.
        answering('cannot');

        const release = aPageHolding(true);

        assert.equal(unsaved.mayBeLost(), true);

        release();

    });

});


describe('a page that has been left', () => {

    it('is no longer asked, because nobody can still save it', () => {

        answering(false);

        const release = aPageHolding(true);
        release();

        assert.equal(unsaved.any(),       false);
        assert.equal(unsaved.mayBeLost(), true);
        assert.deepEqual(asked, []);

    });

    it('does not release the page that came after it', () => {

        answering(false);

        const releaseFirst  = aPageHolding(false);
        const releaseSecond = aPageHolding(true);

        releaseFirst();

        assert.equal(unsaved.any(), true, 'the page that is open stopped being asked');

        releaseSecond();

    });

    it('and a page that cannot answer counts as holding nothing', () => {

        answering(false);

        const release = unsaved.heldBy(() => { throw new Error('halfway through drawing'); });

        assert.equal(unsaved.any(), false);

        release();

    });

});


describe('whether a form has been typed into since it was drawn', () => {

    const field = (value: string, drawnWith: string) =>
        ({ tagName: 'INPUT', type: 'text', value, defaultValue: drawnWith });

    const tick = (checked: boolean, drawnWith: boolean) =>
        ({ tagName: 'INPUT', type: 'checkbox', checked, defaultChecked: drawnWith });

    const picker = (chosen: number, drawnWith: number) =>
        ({ tagName: 'SELECT',
           options: [0, 1, 2].map(index => ({ selected: index === chosen, defaultSelected: index === drawnWith })) });

    const form = (controls: unknown[]) =>
        ({ querySelectorAll: () => controls }) as unknown as ParentNode;

    it('is no, for a form still as the local controller left it', () => {
        assert.equal(typedSinceDrawn(form([field('22:00', '22:00'), tick(true, true), picker(1, 1)])), false);
    });

    it('is yes, for one character in one field', () => {
        assert.equal(typedSinceDrawn(form([field('22:00', '22:00'), field('05:45', '06:00')])), true);
    });

    it('is yes, for a box ticked or unticked', () => {
        assert.equal(typedSinceDrawn(form([tick(false, true)])), true);
    });

    it('is yes, for something else chosen from a list', () => {
        assert.equal(typedSinceDrawn(form([picker(2, 0)])), true);
    });

    it('and is no where there is no form at all', () => {
        // A page halfway through loading has no form to compare, which is not
        // the same thing as a page with something in it.
        assert.equal(typedSinceDrawn(null), false);
        assert.equal(typedSinceDrawn(form([])), false);
    });

});
