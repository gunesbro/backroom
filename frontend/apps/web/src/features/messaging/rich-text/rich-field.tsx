import { useEffect, useId, useImperativeHandle, useRef, useState, type KeyboardEvent, type Ref } from 'react';
import { $createCodeNode, $isCodeNode } from '@lexical/code';
import { $createLinkNode, $isLinkNode, $toggleLink } from '@lexical/link';
import {
  $isListNode,
  INSERT_ORDERED_LIST_COMMAND,
  INSERT_UNORDERED_LIST_COMMAND,
  REMOVE_LIST_COMMAND,
  registerList,
} from '@lexical/list';
import { createEmptyHistoryState, registerHistory } from '@lexical/history';
import {
  BOLD_ITALIC_STAR,
  BOLD_STAR,
  CODE,
  INLINE_CODE,
  ITALIC_STAR,
  ORDERED_LIST,
  QUOTE,
  STRIKETHROUGH,
  UNORDERED_LIST,
  registerMarkdownShortcuts,
} from '@lexical/markdown';
import { $createQuoteNode, $isQuoteNode, registerRichText } from '@lexical/rich-text';
import { $setBlocksType } from '@lexical/selection';
import { $findMatchingParent } from '@lexical/utils';
import {
  $createParagraphNode,
  $createTextNode,
  $getSelection,
  $isRangeSelection,
  CLEAR_HISTORY_COMMAND,
  COMMAND_PRIORITY_HIGH,
  FORMAT_TEXT_COMMAND,
  INSERT_PARAGRAPH_COMMAND,
  KEY_DOWN_COMMAND,
  KEY_ENTER_COMMAND,
  KEY_TAB_COMMAND,
  TextNode,
  createEditor,
  type LexicalEditor,
  type TextFormatType,
} from 'lexical';
import { Icon, Toolbar, type IconName } from '@backroom/ui';
import { useTranslate } from '@/shared/locale';
import { messages } from '../messages';
import { hrefFromInput, type Block } from './blocks';
import { $emojiFromText, $textFromEmoji, EmojiNode } from './emoji-node';
import { $readBlocks, $writeBlocks, EDITOR_NODES } from './lexical-blocks';

/** The editor's content in our block schema. */
export type RichValue = { blocks: Block[] };

export type RichFieldHandle = {
  clear: () => void;
  focus: () => void;
};

type BlockKind = 'paragraph' | 'bullet' | 'number' | 'quote' | 'code';
type ActiveState = {
  bold: boolean;
  italic: boolean;
  strikethrough: boolean;
  code: boolean;
  link: boolean;
  block: BlockKind;
};

const INACTIVE: ActiveState = {
  bold: false,
  italic: false,
  strikethrough: false,
  code: false,
  link: false,
  block: 'paragraph',
};

// Markdown-like shortcuts the design implies: `**bold**`, `*italic*`, `~~strike~~`, `` `code` ``, "- " and "1. " for
// lists, "> " for a quote and three backticks for a code block. Headings and links are not part of the schema.
const SHORTCUTS = [
  BOLD_ITALIC_STAR,
  BOLD_STAR,
  ITALIC_STAR,
  STRIKETHROUGH,
  INLINE_CODE,
  UNORDERED_LIST,
  ORDERED_LIST,
  QUOTE,
  CODE,
];

const UNDO_DEPTH = 100;

const DISALLOWED_FORMATS: readonly TextFormatType[] = [
  'underline',
  'highlight',
  'subscript',
  'superscript',
  'lowercase',
  'uppercase',
  'capitalize',
];

const THEME = {
  text: {
    bold: 'bm-rt-bold',
    italic: 'bm-rt-italic',
    strikethrough: 'bm-rt-strike',
    code: 'bm-inline-code',
  },
  quote: 'bm-quote',
  code: 'bm-code-block',
  link: 'bm-rt-link',
  list: { ul: 'bm-list', ol: 'bm-list', listitem: 'bm-rt-item', nested: { listitem: 'bm-rt-item' } },
};

function readActive(editor: LexicalEditor): ActiveState {
  return editor.read(() => {
    const selection = $getSelection();
    if (!$isRangeSelection(selection)) return INACTIVE;
    const top = selection.anchor.getNode().getTopLevelElement();
    const block: BlockKind = $isListNode(top)
      ? top.getListType() === 'number'
        ? 'number'
        : 'bullet'
      : $isQuoteNode(top)
        ? 'quote'
        : $isCodeNode(top)
          ? 'code'
          : 'paragraph';
    return {
      bold: selection.hasFormat('bold'),
      italic: selection.hasFormat('italic'),
      strikethrough: selection.hasFormat('strikethrough'),
      code: selection.hasFormat('code'),
      link: $findMatchingParent(selection.anchor.getNode(), $isLinkNode) !== null,
      block,
    };
  });
}

function sameActive(a: ActiveState, b: ActiveState): boolean {
  return (
    a.bold === b.bold &&
    a.italic === b.italic &&
    a.strikethrough === b.strikethrough &&
    a.code === b.code &&
    a.link === b.link &&
    a.block === b.block
  );
}

/** The link under the cursor, for the link field to start from. */
function currentHref(editor: LexicalEditor): string {
  return editor.read(() => {
    const selection = $getSelection();
    if (!$isRangeSelection(selection)) return '';
    return $findMatchingParent(selection.anchor.getNode(), $isLinkNode)?.getURL() ?? '';
  });
}

/**
 * The Lexical editor of the composer with its formatting toolbar (docs/design: Main, Thread). It is the only place
 * that imports the editor, so it loads with the composer's own chunk. Content leaves it only as our blocks.
 *
 * - Enter sends, Shift+Enter makes a new line (a new item in a list); Tab is not captured, so keyboard users can leave.
 * - The toolbar has one tab stop (arrow keys move inside), buttons show `aria-pressed`.
 */
export function RichField({
  label,
  placeholder,
  describedBy,
  invalid,
  onChange,
  onSubmit,
  ref,
}: {
  label: string;
  placeholder: string;
  describedBy: string;
  invalid: boolean;
  onChange: (value: RichValue) => void;
  onSubmit: () => void;
  ref?: Ref<RichFieldHandle>;
}) {
  const t = useTranslate(messages);
  const rootRef = useRef<HTMLDivElement>(null);
  const editorRef = useRef<LexicalEditor | null>(null);
  const linkInputRef = useRef<HTMLInputElement>(null);
  const linkId = useId();
  const [active, setActive] = useState<ActiveState>(INACTIVE);
  const [empty, setEmpty] = useState(true);
  const [linkOpen, setLinkOpen] = useState(false);
  const [linkValue, setLinkValue] = useState('');
  const [linkInvalid, setLinkInvalid] = useState(false);
  // Callbacks change on every render of the parent; the editor is created once.
  const callbacks = useRef({ onChange, onSubmit });
  useEffect(() => {
    callbacks.current = { onChange, onSubmit };
  });

  useImperativeHandle(ref, () => ({
    clear: () => {
      const editor = editorRef.current;
      if (!editor) return;
      editor.update(() => {
        $writeBlocks([]);
      });
      editor.dispatchCommand(CLEAR_HISTORY_COMMAND, undefined);
    },
    focus: () => {
      editorRef.current?.focus(undefined, { defaultSelection: 'rootEnd' });
    },
  }));

  const openLink = () => {
    const editor = editorRef.current;
    if (!editor) return;
    setLinkValue(currentHref(editor));
    setLinkInvalid(false);
    setLinkOpen(true);
  };

  useEffect(() => {
    if (linkOpen) linkInputRef.current?.focus();
  }, [linkOpen]);

  useEffect(() => {
    const element = rootRef.current;
    if (!element) return;
    const editor = createEditor({
      namespace: 'composer',
      nodes: [...EDITOR_NODES],
      theme: THEME,
      onError: (error) => {
        throw error;
      },
    });
    editorRef.current = editor;
    editor.setRootElement(element);
    editor.update(
      () => {
        $writeBlocks([]);
      },
      { discrete: true },
    );
    const unregister = [
      registerRichText(editor),
      registerList(editor),
      // Undo is bounded: 100 steps.
      registerHistory(editor, createEmptyHistoryState(), 300, undefined, undefined, UNDO_DEPTH),
      // What cannot be sent is not shown: only bold, italic, strike and code survive (Ctrl+U, pasted styles).
      editor.registerNodeTransform(TextNode, (node) => {
        for (const format of DISALLOWED_FORMATS) {
          if (node.hasFormat(format)) node.toggleFormat(format);
        }
        if (node.getStyle() !== '') node.setStyle('');
      }),
      // Emoji are drawn as images, however they got here: typed, pasted or picked.
      editor.registerNodeTransform(TextNode, $emojiFromText),
      editor.registerNodeTransform(EmojiNode, $textFromEmoji),
      registerMarkdownShortcuts(editor, SHORTCUTS),
      editor.registerCommand(
        KEY_ENTER_COMMAND,
        (event) => {
          if (!event || event.isComposing) return false;
          if (!event.shiftKey) {
            event.preventDefault();
            callbacks.current.onSubmit();
            return true;
          }
          // Shift+Enter: a new item inside a list, a line break everywhere else (the default).
          const inList = editor.read(() => {
            const selection = $getSelection();
            return (
              $isRangeSelection(selection) && $isListNode(selection.anchor.getNode().getTopLevelElement())
            );
          });
          if (!inList) return false;
          event.preventDefault();
          return editor.dispatchCommand(INSERT_PARAGRAPH_COMMAND, undefined);
        },
        COMMAND_PRIORITY_HIGH,
      ),
      // The editor must not trap the keyboard: Tab leaves it (the browser moves the focus), nothing is indented.
      editor.registerCommand(KEY_TAB_COMMAND, () => true, COMMAND_PRIORITY_HIGH),
      editor.registerCommand(
        KEY_DOWN_COMMAND,
        (event) => {
          if (
            (event.ctrlKey || event.metaKey) &&
            !event.shiftKey &&
            !event.altKey &&
            event.key.toLowerCase() === 'k'
          ) {
            event.preventDefault();
            setLinkValue(currentHref(editor));
            setLinkInvalid(false);
            setLinkOpen(true);
            return true;
          }
          return false;
        },
        COMMAND_PRIORITY_HIGH,
      ),
      editor.registerUpdateListener(({ editorState, dirtyElements, dirtyLeaves }) => {
        const next = readActive(editor);
        setActive((current) => (sameActive(current, next) ? current : next));
        if (dirtyElements.size === 0 && dirtyLeaves.size === 0) return; // only the selection moved
        const blocks = editorState.read($readBlocks);
        setEmpty(blocks.length === 0);
        callbacks.current.onChange({ blocks });
      }),
    ];
    return () => {
      for (const off of unregister) off();
      editor.setRootElement(null);
      editorRef.current = null;
    };
  }, []);

  const run = (action: (editor: LexicalEditor) => void) => {
    const editor = editorRef.current;
    if (!editor) return;
    action(editor);
    editor.focus();
  };
  const format = (type: TextFormatType) => {
    run((editor) => editor.dispatchCommand(FORMAT_TEXT_COMMAND, type));
  };
  const list = (kind: 'bullet' | 'number') => {
    run((editor) =>
      editor.dispatchCommand(
        active.block === kind
          ? REMOVE_LIST_COMMAND
          : kind === 'bullet'
            ? INSERT_UNORDERED_LIST_COMMAND
            : INSERT_ORDERED_LIST_COMMAND,
        undefined,
      ),
    );
  };
  const block = (kind: 'quote' | 'code') => {
    run((editor) => {
      editor.update(() => {
        const selection = $getSelection();
        if (!$isRangeSelection(selection)) return;
        $setBlocksType(selection, () =>
          active.block === kind
            ? $createParagraphNode()
            : kind === 'quote'
              ? $createQuoteNode()
              : $createCodeNode(),
        );
      });
    });
  };

  const applyLink = () => {
    const href = hrefFromInput(linkValue);
    if (!href) {
      setLinkInvalid(true);
      return;
    }
    const editor = editorRef.current;
    if (!editor) return;
    editor.update(() => {
      const selection = $getSelection();
      if (!$isRangeSelection(selection)) return;
      if (selection.isCollapsed() && !$findMatchingParent(selection.anchor.getNode(), $isLinkNode)) {
        // Nothing selected: the address itself becomes the link text.
        const link = $createLinkNode(href);
        link.append($createTextNode(href));
        selection.insertNodes([link]);
      } else {
        $toggleLink(href);
      }
    });
    closeLink();
  };
  const removeLink = () => {
    editorRef.current?.update(() => {
      $toggleLink(null);
    });
    closeLink();
  };
  const closeLink = () => {
    setLinkOpen(false);
    editorRef.current?.focus();
  };
  const linkKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter' && !event.nativeEvent.isComposing) {
      // Never submit the composer from here.
      event.preventDefault();
      applyLink();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      closeLink();
    }
  };

  const button = (
    key: string,
    name: string,
    content: React.ReactNode,
    pressed: boolean,
    onClick: () => void,
    shortcut?: string,
  ) => (
    <button
      key={key}
      type="button"
      aria-label={name}
      title={shortcut ? `${name} (${shortcut})` : name}
      aria-pressed={pressed}
      // Keep the focus (and so the selection) in the editor while a button is pressed.
      onMouseDown={(event) => {
        event.preventDefault();
      }}
      onClick={onClick}
    >
      {content}
    </button>
  );
  const icon = (name: IconName) => <Icon name={name} size={16} />;
  const letter = (key: string, text: string) => (
    <span className={`bm-format__${key}`} aria-hidden="true">
      {text}
    </span>
  );

  return (
    <>
      <Toolbar label={t('formatting')} className="bm-format">
        <span className="bm-format__group">
          {button(
            'bold',
            t('bold'),
            letter('bold', 'B'),
            active.bold,
            () => {
              format('bold');
            },
            'Ctrl+B',
          )}
          {button(
            'italic',
            t('italic'),
            letter('italic', 'I'),
            active.italic,
            () => {
              format('italic');
            },
            'Ctrl+I',
          )}
          {button(
            'strikethrough',
            t('strikethrough'),
            letter('strikethrough', 'S'),
            active.strikethrough,
            () => {
              format('strikethrough');
            },
          )}
        </span>
        <span className="bm-format__group">
          <span className="bm-format__sep" aria-hidden="true" />
          {button('link', t('link'), icon('link'), active.link, openLink, 'Ctrl+K')}
          {button('bulletedList', t('bulletedList'), icon('bulletList'), active.block === 'bullet', () => {
            list('bullet');
          })}
          {button('numberedList', t('numberedList'), icon('numberedList'), active.block === 'number', () => {
            list('number');
          })}
          {button('quote', t('quote'), icon('quote'), active.block === 'quote', () => {
            block('quote');
          })}
        </span>
        <span className="bm-format__group">
          <span className="bm-format__sep" aria-hidden="true" />
          {button('code', t('code'), icon('code'), active.code, () => {
            format('code');
          })}
          {button('codeBlock', t('codeBlock'), icon('codeBlock'), active.block === 'code', () => {
            block('code');
          })}
        </span>
      </Toolbar>
      {linkOpen && (
        <div className="bm-link-field" role="group" aria-label={t('link')}>
          <input
            ref={linkInputRef}
            type="text"
            inputMode="url"
            autoComplete="off"
            spellCheck={false}
            aria-label={t('linkAddress')}
            aria-invalid={linkInvalid ? true : undefined}
            aria-describedby={linkInvalid ? `${linkId}-error` : undefined}
            placeholder="https://"
            value={linkValue}
            onChange={(event) => {
              setLinkValue(event.target.value);
              setLinkInvalid(false);
            }}
            onKeyDown={linkKeyDown}
          />
          <button type="button" onClick={applyLink}>
            {t('linkApply')}
          </button>
          {active.link && (
            <button type="button" onClick={removeLink}>
              {t('linkRemove')}
            </button>
          )}
          <button type="button" onClick={closeLink}>
            {t('cancel')}
          </button>
          {linkInvalid && (
            <p id={`${linkId}-error`} className="bm-link-field__error" role="alert">
              {t('linkInvalid')}
            </p>
          )}
        </div>
      )}
      <div className="bm-rt">
        {empty && (
          <span className="bm-rt__placeholder" aria-hidden="true">
            {placeholder}
          </span>
        )}
        <div
          ref={rootRef}
          className="bm-composer__input bm-rt__input"
          role="textbox"
          aria-multiline="true"
          aria-placeholder={placeholder}
          aria-label={label}
          aria-describedby={describedBy}
          aria-invalid={invalid ? true : undefined}
        />
      </div>
    </>
  );
}
