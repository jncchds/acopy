export default function ToggleField({ checked, onChange, label, hint, disabled }: {
  checked: boolean
  onChange: (checked: boolean) => void
  label: string
  hint?: string
  disabled?: boolean
}) {
  return (
    <label className={'toggle-field' + (disabled ? ' toggle-field-disabled' : '')}>
      <input type="checkbox" className="toggle-input" checked={checked} disabled={disabled}
             onChange={e => onChange(e.target.checked)} />
      <span className="toggle-track" aria-hidden="true"><span className="toggle-thumb" /></span>
      <span className="toggle-text">
        <span className="toggle-label">{label}</span>
        {hint && <span className="toggle-hint">{hint}</span>}
      </span>
    </label>
  )
}
