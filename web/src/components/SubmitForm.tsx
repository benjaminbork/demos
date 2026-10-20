import { useState, type FormEvent } from 'react';
import { Button, MenuItem, Paper, Stack, TextField } from '@mui/material';
import AutoAwesomeIcon from '@mui/icons-material/AutoAwesome';
import type { Voice } from '../api';

interface Props {
  voices: Voice[];
  defaultVoice: string;
  onSubmit: (url: string, voice: string) => Promise<void>;
}

const isValidUrl = (value: string) => {
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
};

export default function SubmitForm({ voices, defaultVoice, onSubmit }: Props) {
  const [url, setUrl] = useState('');
  const [voice, setVoice] = useState('');
  const [touched, setTouched] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const trimmed = url.trim();
  const invalid = touched && trimmed.length > 0 && !isValidUrl(trimmed);
  const selectedVoice = voice || defaultVoice;

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setTouched(true);
    if (!isValidUrl(trimmed) || submitting) return;
    setSubmitting(true);
    setUrl('');
    setTouched(false);
    try {
      await onSubmit(trimmed, selectedVoice);
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <Paper variant="outlined" component="form" onSubmit={handleSubmit} sx={{ p: 2 }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5}>
        <TextField
          fullWidth
          autoFocus
          label="Article URL"
          placeholder="https://…"
          type="url"
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          onBlur={() => setTouched(true)}
          error={invalid}
          helperText={invalid ? 'Please enter a full http(s) address' : undefined}
        />
        {voices.length > 1 && (
          <TextField
            select
            label="Voice"
            value={selectedVoice}
            onChange={(e) => setVoice(e.target.value)}
            sx={{ minWidth: { sm: 220 } }}
          >
            {voices.map((v) => (
              <MenuItem key={v.id} value={v.id}>
                {v.label}
              </MenuItem>
            ))}
          </TextField>
        )}
        <Button
          type="submit"
          variant="contained"
          size="large"
          startIcon={<AutoAwesomeIcon />}
          disabled={trimmed.length === 0 || submitting}
          sx={{ px: 3, minHeight: 56, flexShrink: 0 }}
        >
          Transform
        </Button>
      </Stack>
    </Paper>
  );
}
