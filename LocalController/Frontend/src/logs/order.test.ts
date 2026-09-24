/**
 * The pairing of a drawn line to the entry it shows.
 *
 * Run with `npm test`, which is Node's own runner reading the TypeScript as it
 * stands - no bundler, no browser, no dependency that is not already here.
 *
 * What is pinned is the one thing that breaks silently. The page draws newest
 * first and filters by position, so if drawOrder and entryAt ever disagree,
 * every line shows one entry while being filtered by another and the screen
 * says nothing about it.
 */

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import { drawOrder, entryAt } from './order.ts';


describe('the log draws newest first', () => {

    it('puts the last entry the store holds at the top', () => {
        assert.deepEqual(drawOrder(['oldest', 'middle', 'newest']),
                         ['newest', 'middle', 'oldest']);
    });

    it('leaves the array the store holds alone', () => {

        const entries = ['oldest', 'newest'];

        drawOrder(entries);

        assert.deepEqual(entries, ['oldest', 'newest'],
                         'the store keeps its entries oldest first and must not be reversed under it');

    });

    it('has nothing to draw for an empty store', () => {
        assert.deepEqual(drawOrder([]), []);
    });

});


describe('a line and the entry it shows', () => {

    it('agree at every position', () => {

        const entries = ['a', 'b', 'c', 'd', 'e'];
        const drawn   = drawOrder(entries);

        for (let line = 0; line < drawn.length; line++)
            assert.equal(entryAt(entries, line), drawn[line],
                         `line ${line} shows one entry and would be filtered by another`);

    });

    it('agree for a single entry, where a reversal hides its own mistakes', () => {

        const entries = ['only'];

        assert.equal(entryAt(entries, 0), drawOrder(entries)[0]);

    });

    it('agree again after entries arrive at the top', () => {

        // What append does: the store gains two at its end, the list gains the
        // same two reversed at its front.
        const entries = ['a', 'b', 'c'];
        entries.push('d', 'e');

        const drawn = drawOrder(entries);

        assert.deepEqual(drawn, ['e', 'd', 'c', 'b', 'a']);

        for (let line = 0; line < drawn.length; line++)
            assert.equal(entryAt(entries, line), drawn[line]);

    });

    it('has no entry for a line beyond what the store holds', () => {

        // The page keeps the two the same length, so this should not arise -
        // and if it ever does, an undefined is a great deal easier to find
        // than the entry from the far end of the list.
        assert.equal(entryAt(['a', 'b'], 2), undefined);
        assert.equal(entryAt([],        0), undefined);

    });

});
