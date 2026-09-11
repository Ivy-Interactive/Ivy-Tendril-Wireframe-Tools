import { useCallback, useEffect, useRef, useState } from "react";
import { EditorView, keymap, lineNumbers, highlightActiveLine } from "@codemirror/view";
import { EditorState, type Extension } from "@codemirror/state";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import {
  HighlightStyle,
  syntaxHighlighting,
  indentOnInput,
  bracketMatching,
} from "@codemirror/language";
import { javascript } from "@codemirror/lang-javascript";
import { tags } from "@lezer/highlight";
import { Check, FileCode2, Save, SquareArrowOutUpRight } from "lucide-react";
import { api, type SourceFile } from "../api";
import { Button, Empty, IconButton, cx, formatBytes } from "./ui";

/** Matches the Studio's own palette rather than importing a stock CodeMirror theme, so the
 *  editor does not look bolted on. */
const highlight = HighlightStyle.define([
  { tag: tags.keyword, color: "oklch(0.75 0.14 300)" },
  { tag: [tags.name, tags.deleted, tags.character, tags.macroName], color: "oklch(0.87 0.008 264)" },
  { tag: [tags.propertyName], color: "oklch(0.8 0.1 220)" },
  { tag: [tags.string, tags.special(tags.string)], color: "oklch(0.8 0.12 145)" },
  { tag: [tags.number, tags.bool, tags.null], color: "oklch(0.82 0.12 60)" },
  { tag: [tags.typeName, tags.className, tags.tagName], color: "oklch(0.82 0.11 200)" },
  { tag: [tags.function(tags.variableName), tags.labelName], color: "oklch(0.84 0.11 250)" },
  { tag: [tags.comment, tags.blockComment], color: "oklch(0.5 0.012 264)", fontStyle: "italic" },
  { tag: [tags.operator, tags.punctuation], color: "oklch(0.65 0.012 264)" },
  { tag: [tags.attributeName], color: "oklch(0.8 0.1 220)" },
  { tag: [tags.attributeValue], color: "oklch(0.8 0.12 145)" },
]);

const baseExtensions: Extension[] = [
  lineNumbers(),
  highlightActiveLine(),
  history(),
  indentOnInput(),
  bracketMatching(),
  syntaxHighlighting(highlight),
  keymap.of([...defaultKeymap, ...historyKeymap, indentWithTab]),
  javascript({ jsx: true, typescript: true }),
  EditorView.lineWrapping,
  EditorView.theme({ "&": { height: "100%" } }),
];

export function CodePanel({
  project,
  files,
  filesVersion,
}: {
  project: string | null;
  files: SourceFile[];
  /** Bumped when the watcher reports a change, so the open file re-reads from disk. */
  filesVersion: number;
}) {
  const [active, setActive] = useState<string | null>(null);
  const [dirty, setDirty] = useState(false);
  const [saved, setSaved] = useState(false);
  const [editorError, setEditorError] = useState<string | null>(null);

  const viewRef = useRef<EditorView | null>(null);
  // Bumped when the editor attaches, so the file-loading effect below re-runs once there
  // is actually a view to write into.
  const [ready, setReady] = useState(0);
  // Tracks what is on disk, so an external change can be distinguished from our own edit.
  const diskRef = useRef<string>("");

  // Prefer App.tsx: it is where the wireframe actually lives.
  useEffect(() => {
    if (!files.length) {
      setActive(null);
      return;
    }
    if (active && files.some((f) => f.path === active)) return;
    const preferred =
      files.find((f) => f.path.endsWith("App.tsx")) ??
      files.find((f) => f.path.endsWith("main.tsx")) ??
      files[0];
    setActive(preferred.path);
  }, [files, active]);

  // A callback ref rather than an effect: the host div does not exist on first render
  // (the empty state is showing instead), and an effect with empty deps would run once,
  // find no node, and never try again -- which leaves the editor permanently unmounted.
  const attachEditor = useCallback((node: HTMLDivElement | null) => {
    if (!node) {
      viewRef.current?.destroy();
      viewRef.current = null;
      return;
    }
    if (viewRef.current) return;

    viewRef.current = new EditorView({
      parent: node,
      state: EditorState.create({
        doc: "",
        extensions: [
          ...baseExtensions,
          EditorView.updateListener.of((update) => {
            if (update.docChanged) {
              setDirty(update.state.doc.toString() !== diskRef.current);
              setSaved(false);
            }
          }),
        ],
      }),
    });
    setReady((n) => n + 1);
  }, []);

  // Load the selected file, and reload it when the watcher says it changed on disk.
  useEffect(() => {
    if (!project || !active) return;
    let cancelled = false;

    void (async () => {
      try {
        const content = await api.readFile(project, active);
        if (cancelled) return;

        const view = viewRef.current;
        if (!view) return;

        // Never clobber unsaved edits with a disk reload the user did not ask for.
        const current = view.state.doc.toString();
        if (current !== diskRef.current && current !== content) {
          diskRef.current = content;
          setDirty(true);
          return;
        }

        diskRef.current = content;
        view.dispatch({
          changes: { from: 0, to: view.state.doc.length, insert: content },
        });
        setDirty(false);
      } catch (error) {
        console.error(error);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [project, active, filesVersion, ready]);

  const save = async () => {
    const view = viewRef.current;
    if (!project || !active || !view) return;
    const content = view.state.doc.toString();
    await api.writeFile(project, active, content);
    diskRef.current = content;
    setDirty(false);
    setSaved(true);
    setTimeout(() => setSaved(false), 1600);
  };

  // Ctrl/Cmd+S saves. Autosave is deliberately absent: a half-typed JSX expression would
  // break the bundle and blank the preview on every keystroke.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") {
        e.preventDefault();
        void save();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  const current = files.find((f) => f.path === active);

  const openInEditor = async () => {
    if (!project) return;
    try {
      // Hand over the open file when there is one, otherwise the whole project.
      await api.openInEditor(project, active ?? undefined);
    } catch (error) {
      setEditorError(error instanceof Error ? error.message : String(error));
    }
  };

  return (
    <div className="flex h-full min-h-0 flex-col">
      {!project || files.length === 0 ? (
        <Empty icon={<FileCode2 size={20} />}>
          {project ? "No source files in src/." : "Pick a wireframe on the left."}
        </Empty>
      ) : (
        <>
          <div className="flex shrink-0 items-center gap-px overflow-x-auto border-b border-edge
                          bg-shell-sunken pr-2">
            {files.map((file) => {
              const isActive = file.path === active;
              return (
                <button
                  key={file.path}
                  type="button"
                  onClick={() => setActive(file.path)}
                  className={cx(
                    "shrink-0 border-b-2 px-3 py-1.5 font-mono text-[11.5px] transition-colors",
                    isActive
                      ? "border-brand bg-shell text-body"
                      : "border-transparent text-body-faint hover:text-body-muted"
                  )}
                >
                  {file.path.replace(/^src\//, "")}
                  {isActive && dirty && <span className="ml-1.5 text-brand">●</span>}
                </button>
              );
            })}
            <div className="ml-auto flex shrink-0 items-center gap-2 pl-3">
              {current && (
                <span className="font-mono text-[10.5px] text-body-faint">
                  {formatBytes(current.bytes)}
                </span>
              )}
              <IconButton
                icon={<SquareArrowOutUpRight size={13} />}
                label="Open in VS Code"
                onClick={() => void openInEditor()}
                disabled={!project}
              />
              <Button
                onClick={save}
                disabled={!dirty}
                variant={dirty ? "primary" : "ghost"}
                icon={saved ? <Check size={13} /> : <Save size={13} />}
              >
                {saved ? "Saved" : dirty ? "Save" : "Saved"}
              </Button>
            </div>
          </div>
          {/* A definite height is required: CodeMirror measures its host, and a flex
              child without min-h-0 collapses to zero here, which renders nothing. */}
          {editorError && (
            <div className="flex shrink-0 items-start gap-2 border-b border-edge bg-warn/10 px-3 py-2
                            text-[11.5px] leading-relaxed text-warn">
              <span className="flex-1">{editorError}</span>
              <button
                type="button"
                onClick={() => setEditorError(null)}
                className="shrink-0 text-warn/70 hover:text-warn"
              >
                dismiss
              </button>
            </div>
          )}
          <div ref={attachEditor} className="min-h-0 flex-1 overflow-hidden" />
        </>
      )}
    </div>
  );
}
