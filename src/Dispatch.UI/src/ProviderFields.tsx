import { type ProviderField } from "./lib/providers";

// Renders a provider's credential fields: a dropdown when the field declares `options` (e.g. Region),
// a password box for secrets, otherwise a text box. Shared by the add-relay wizard and first-run wizard.
export function ProviderFieldsInput({ fields, values, onChange }: {
  fields: ProviderField[]; values: Record<string, string>; onChange: (v: Record<string, string>) => void;
}) {
  const set = (name: string, value: string) => onChange({ ...values, [name]: value });
  return (
    <>
      {fields.map((f) => (
        <label key={f.name} style={{ display: "block", margin: "10px 0" }}>
          <div style={{ fontSize: 13 }}>{f.label ?? f.name}{f.required ? " *" : ""}</div>
          {f.options
            ? (
              <select value={values[f.name] ?? ""} onChange={(e) => set(f.name, e.target.value)} style={{ width: "100%" }}>
                <option value="">{f.required ? "— select —" : "(default)"}</option>
                {f.options.map((o) => <option key={o} value={o}>{o}</option>)}
              </select>
            )
            : (
              <input type={f.secret ? "password" : "text"} placeholder={f.placeholder}
                value={values[f.name] ?? ""} onChange={(e) => set(f.name, e.target.value)} style={{ width: "100%" }}
                // These are provider credentials, not the user's login — don't let the browser or password
                // managers offer to save/autofill them. "new-password" suppresses autofill of saved logins.
                autoComplete={f.secret ? "new-password" : "off"} spellCheck={false}
                data-1p-ignore data-lpignore="true" data-bwignore data-form-type="other" />
            )}
          {f.help && <div className="muted" style={{ fontSize: 12, marginTop: 4 }}>{f.help}</div>}
        </label>
      ))}
    </>
  );
}
