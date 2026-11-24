import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import axe from 'axe-core';
import { $selectAll, type LexicalEditor } from 'lexical';
import { describe, expect, it, vi } from 'vitest';
import { LocaleProvider } from '@/shared/locale-provider';
import { Composer } from './composer';
import type { Block } from './rich-text/blocks';
import { $readBlocks, $writeBlocks } from './rich-text/lexical-blocks';

vi.mock('@/config', async (importOriginal) => {
  const original = await importOriginal<typeof import('@/config')>();
  return { config: { ...original.config, richTextEnabled: true } };
});

type WithEditor = HTMLElement & { __lexicalEditor?: LexicalEditor };

function paragraph(text: string): Block[] {
  return [{ type: 'paragraph', children: [{ type: 'text', text }] }];
}

async function setup() {
  const onSend = vi.fn<(text: string, blocks?: Block[]) => void>();
  const view = render(
    <LocaleProvider>
      <Composer channelName="general" onSend={onSend} />
    </LocaleProvider>,
  );
  const input = await screen.findByRole<WithEditor>('textbox', { name: 'Message #general' });
  const editor = input.__lexicalEditor as LexicalEditor;
  const write = (blocks: Block[]) => {
    act(() => {
      editor.update(
        () => {
          $writeBlocks(blocks);
        },
        { discrete: true },
      );
    });
  };
  const blocks = () => editor.read($readBlocks);
  return { ...view, onSend, input, editor, write, blocks };
}

describe('Rich text composer', () => {
  it('Composer_WhenEmpty_DisablesSendAndExposesTextboxAndToolbar', async () => {
    await setup();
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
    const textbox = screen.getByRole('textbox', { name: 'Message #general' });
    expect(textbox).toHaveAttribute('aria-multiline', 'true');
    const toolbar = screen.getByRole('toolbar', { name: 'Formatting' });
    const stops = within(toolbar)
      .getAllByRole('button')
      .filter((button) => button.tabIndex === 0);
    expect(stops).toHaveLength(1);
  });

  it('Composer_WhenEnterPressed_SendsTextAndBlocksAndClears', async () => {
    const { onSend, input, write, blocks } = await setup();
    write(paragraph('Hello İstanbul'));
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled();
    });

    fireEvent.keyDown(input, { key: 'Enter' });

    expect(onSend).toHaveBeenCalledExactlyOnceWith('Hello İstanbul', paragraph('Hello İstanbul'));
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
    });
    expect(blocks()).toEqual([]);
  });

  it('Composer_WhenShiftEnterPressed_DoesNotSend', async () => {
    const { onSend, input, write } = await setup();
    write(paragraph('draft'));

    fireEvent.keyDown(input, { key: 'Enter', shiftKey: true });

    expect(onSend).not.toHaveBeenCalled();
  });

  it('Composer_WhenEnterWhileEmpty_SendsNothing', async () => {
    const { onSend, input } = await setup();
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onSend).not.toHaveBeenCalled();
  });

  it('Composer_WhenEnterDuringComposition_DoesNotSend', async () => {
    const { onSend, input, write } = await setup();
    write(paragraph('draft'));
    fireEvent.keyDown(input, { key: 'Enter', isComposing: true });
    expect(onSend).not.toHaveBeenCalled();
  });

  it('Composer_WhenOver4000Characters_DisablesSendAndShowsCount', async () => {
    const { onSend, input, write } = await setup();
    write(paragraph('a'.repeat(4001)));

    await waitFor(() => {
      expect(screen.getByText(/1 characters over the limit/)).toBeInTheDocument();
    });
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
    expect(input).toHaveAttribute('aria-invalid', 'true');
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onSend).not.toHaveBeenCalled();
  });

  it('Composer_WhenExactly4000Characters_Sends', async () => {
    const { onSend, input, write } = await setup();
    write(paragraph('a'.repeat(4000)));
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled();
    });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onSend).toHaveBeenCalledOnce();
  });

  it('Composer_WhenBoldPressedWithSelection_MarksTheText', async () => {
    const { editor, write, blocks } = await setup();
    write(paragraph('bold me'));
    act(() => {
      editor.update(
        () => {
          $selectAll();
        },
        { discrete: true },
      );
    });

    fireEvent.click(screen.getByRole('button', { name: 'Bold' }));

    expect(blocks()).toEqual([
      { type: 'paragraph', children: [{ type: 'text', text: 'bold me', marks: [{ type: 'bold' }] }] },
    ]);
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Bold' })).toHaveAttribute('aria-pressed', 'true');
    });
  });

  it('Composer_WhenListAndQuoteAndCodeBlockPressed_ChangesTheBlock', async () => {
    const { editor, write, blocks } = await setup();
    const select = () => {
      act(() => {
        editor.update(
          () => {
            $selectAll();
          },
          { discrete: true },
        );
      });
    };
    write(paragraph('item'));
    select();
    fireEvent.click(screen.getByRole('button', { name: 'Bulleted list' }));
    expect(blocks().map((block) => block.type)).toEqual(['bulleted_list']);
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Bulleted list' })).toHaveAttribute('aria-pressed', 'true');
    });

    fireEvent.click(screen.getByRole('button', { name: 'Bulleted list' }));
    expect(blocks().map((block) => block.type)).toEqual(['paragraph']);

    select();
    fireEvent.click(screen.getByRole('button', { name: 'Quote' }));
    expect(blocks().map((block) => block.type)).toEqual(['quote']);

    select();
    fireEvent.click(screen.getByRole('button', { name: 'Code block' }));
    expect(blocks().map((block) => block.type)).toEqual(['code_block']);
  });

  it('Composer_WhenLinkAddressIsJavascript_RefusesIt', async () => {
    const { editor, write, blocks } = await setup();
    write(paragraph('link me'));
    act(() => {
      editor.update(
        () => {
          $selectAll();
        },
        { discrete: true },
      );
    });
    fireEvent.click(screen.getByRole('button', { name: 'Link' }));
    const address = screen.getByRole('textbox', { name: 'Link address' });

    fireEvent.change(address, { target: { value: 'javascript:alert(1)' } });
    fireEvent.keyDown(address, { key: 'Enter' });

    expect(screen.getByRole('alert')).toHaveTextContent('Enter a valid web or email address');
    expect(JSON.stringify(blocks())).not.toContain('marks');

    fireEvent.change(address, { target: { value: 'example.com' } });
    fireEvent.keyDown(address, { key: 'Enter' });

    expect(blocks()).toEqual([
      {
        type: 'paragraph',
        children: [
          { type: 'text', text: 'link me', marks: [{ type: 'link', href: 'https://example.com/' }] },
        ],
      },
    ]);
  });

  it('Composer_WhenCtrlKPressed_OpensTheLinkField', async () => {
    const { input } = await setup();
    fireEvent.keyDown(input, { key: 'k', ctrlKey: true });
    expect(await screen.findByRole('textbox', { name: 'Link address' })).toBeInTheDocument();
  });

  it('Composer_WhenTabPressed_DoesNotCaptureTheKey', async () => {
    const { input, write } = await setup();
    write(paragraph('x'));
    const notPrevented = fireEvent.keyDown(input, { key: 'Tab' });
    expect(notPrevented).toBe(true);
  });

  it('Composer_WhenRendered_HasNoAccessibilityViolations', async () => {
    const { container, write } = await setup();
    write(paragraph('hello'));
    const results = await axe.run(container, {
      rules: { 'color-contrast': { enabled: false }, region: { enabled: false } },
    });
    expect(results.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
  });
});
