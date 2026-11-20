import { useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  Collapse,
  LinearProgress,
  Link,
  MenuItem,
  Stack,
  Step,
  StepLabel,
  Stepper,
  TextField,
  Typography,
} from '@mui/material';
import DownloadIcon from '@mui/icons-material/Download';
import ReplayIcon from '@mui/icons-material/Replay';
import RecordVoiceOverIcon from '@mui/icons-material/RecordVoiceOver';
import { api, apiUrl, type Article, type Phase, type Step as PipelineStep, type Voice } from '../api';
import { formatDuration, formatSize, timeAgo, secondsUntil } from '../format';

interface Props {
  article: Article;
  voices: Voice[];
  onRetry: (id: string) => Promise<void>;
  onChangeVoice: (id: string, voice: string) => Promise<void>;
}

const phaseLabel: Record<Phase, string> = {
  reading: 'Reading article…',
  generating: 'Generating audio…',
  ready: 'Ready',
  failed: 'Failed',
};

const phaseColor: Record<Phase, 'default' | 'primary' | 'success' | 'error'> = {
  reading: 'primary',
  generating: 'primary',
  ready: 'success',
  failed: 'error',
};

const failedWhile: Record<PipelineStep, string> = {
  extract: 'while reading the page',
  rewrite: 'while preparing the spoken text',
  synthesize: 'while generating the audio',
};

const stepIndex: Record<Phase, number> = { reading: 0, generating: 1, ready: 3, failed: -1 };

export default function ArticleCard({ article, voices, onRetry, onChangeVoice }: Props) {
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string>();
  const [fullText, setFullText] = useState<string>();
  const [showFull, setShowFull] = useState(false);
  const [voice, setVoice] = useState(article.voice);

  const active = article.status === 'queued' || article.status === 'processing';
  const title = article.title ?? article.url;
  const voiceLabel = (id: string) => voices.find((v) => v.id === id)?.label ?? id;

  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setActionError(undefined);
    try {
      await action();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const toggleFullText = async () => {
    if (!showFull && fullText === undefined) {
      try {
        setFullText(await api.getText(article.id));
      } catch (e) {
        setActionError(e instanceof Error ? e.message : String(e));
        return;
      }
    }
    setShowFull((s) => !s);
  };

  return (
    <Card>
      <CardContent sx={{ '&:last-child': { pb: 2 } }}>
        <Stack direction="row" spacing={2} sx={{ alignItems: 'flex-start' }}>
          <Box sx={{ minWidth: 0, flex: 1 }}>
            <Link
              href={article.url}
              target="_blank"
              rel="noopener noreferrer"
              underline="hover"
              color="inherit"
              variant="subtitle1"
              sx={{ fontWeight: 600, display: 'block', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
              title={article.url}
            >
              {title}
            </Link>
            <Typography variant="body2" color="text.secondary" noWrap>
              {[article.siteName, article.byline, timeAgo(article.createdAt)].filter(Boolean).join(' · ')}
            </Typography>
          </Box>
          <Chip size="small" label={phaseLabel[article.phase]} color={phaseColor[article.phase]} variant={active ? 'outlined' : 'filled'} />
        </Stack>

        {active && <Progress article={article} />}

        {article.failure && (
          <Alert
            severity="error"
            sx={{ mt: 2 }}
            action={
              <Button color="inherit" size="small" startIcon={<ReplayIcon />} disabled={busy} onClick={() => run(() => onRetry(article.id))}>
                Retry
              </Button>
            }
          >
            <strong>{article.failure.message}</strong>
            <Typography variant="body2" component="span" sx={{ display: 'block', opacity: 0.8 }}>
              Failed {failedWhile[article.failure.step]}. Retry continues from this step.
            </Typography>
          </Alert>
        )}

        {article.spokenPreview && (
          <Box sx={{ mt: 2, p: 1.5, borderRadius: 2, bgcolor: 'action.hover' }}>
            <Typography variant="overline" color="text.secondary" sx={{ lineHeight: 1.5 }}>
              Spoken text {article.spokenChars ? `· ${Math.round(article.spokenChars / 900)} min read aloud` : ''}
            </Typography>
            <Collapse in={!showFull}>
              <Typography variant="body2" sx={{ whiteSpace: 'pre-line', display: '-webkit-box', WebkitLineClamp: 4, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}>
                {article.spokenPreview}
              </Typography>
            </Collapse>
            <Collapse in={showFull} unmountOnExit>
              <Typography variant="body2" sx={{ whiteSpace: 'pre-line', maxHeight: 360, overflowY: 'auto' }}>
                {fullText}
              </Typography>
            </Collapse>
            <Button size="small" onClick={toggleFullText} sx={{ mt: 0.5, ml: -1 }}>
              {showFull ? 'Show less' : 'Show full text'}
            </Button>
          </Box>
        )}

        {article.audio && (
          <Box sx={{ mt: 2 }}>
            <audio controls preload="none" src={apiUrl(article.audio.url)} style={{ width: '100%' }} />
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} sx={{ mt: 1, alignItems: { sm: 'center' } }}>
              <Button variant="outlined" startIcon={<DownloadIcon />} href={apiUrl(article.audio.downloadUrl)}>
                Download MP3
              </Button>
              <Typography variant="body2" color="text.secondary" sx={{ flex: 1 }}>
                {[voiceLabel(article.audio.voice), formatDuration(article.audio.durationSeconds), formatSize(article.audio.sizeBytes)].filter(Boolean).join(' · ')}
              </Typography>
              {voices.length > 1 && (
                <Stack direction="row" spacing={1}>
                  <TextField select size="small" label="Voice" value={voice} onChange={(e) => setVoice(e.target.value)} sx={{ minWidth: 200 }} disabled={active}>
                    {voices.map((v) => (
                      <MenuItem key={v.id} value={v.id}>
                        {v.label}
                      </MenuItem>
                    ))}
                  </TextField>
                  <Button
                    startIcon={<RecordVoiceOverIcon />}
                    disabled={busy || active || voice === article.audio.voice}
                    onClick={() => run(() => onChangeVoice(article.id, voice))}
                  >
                    Regenerate
                  </Button>
                </Stack>
              )}
            </Stack>
          </Box>
        )}

        {actionError && (
          <Alert severity="warning" sx={{ mt: 2 }} onClose={() => setActionError(undefined)}>
            {actionError}
          </Alert>
        )}
      </CardContent>
    </Card>
  );
}

function Progress({ article }: { article: Article }) {
  const progress = article.phase === 'generating' && article.progress?.total ? article.progress : undefined;
  const retryIn = secondsUntil(article.retry?.nextAttemptAt);

  let detail = article.phase === 'reading' && article.hasSpokenText === false && article.step === 'rewrite' ? 'Preparing the spoken text' : undefined;
  if (progress) detail = `Part ${Math.min(progress.done + 1, progress.total)} of ${progress.total}`;

  return (
    <Box sx={{ mt: 2 }}>
      <Stepper activeStep={stepIndex[article.phase]} alternativeLabel sx={{ mb: 1.5, display: { xs: 'none', sm: 'flex' } }}>
        <Step><StepLabel>Reading article</StepLabel></Step>
        <Step><StepLabel>Generating audio</StepLabel></Step>
        <Step><StepLabel>Ready</StepLabel></Step>
      </Stepper>
      <LinearProgress
        variant={progress ? 'determinate' : 'indeterminate'}
        value={progress ? (progress.done / progress.total) * 100 : undefined}
        sx={{ borderRadius: 1, height: 6 }}
      />
      <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
        {phaseLabel[article.phase]}
        {detail ? ` ${detail}` : ''}
      </Typography>
      {article.retry && (
        <Typography variant="body2" color="warning.main" sx={{ mt: 0.5 }}>
          Temporary problem: {article.retry.lastError}.{' '}
          {retryIn !== undefined && retryIn > 0
            ? `Trying again in ${retryIn}s (attempt ${article.retry.attempt} of ${article.retry.maxAttempts}).`
            : `Trying again (attempt ${article.retry.attempt} of ${article.retry.maxAttempts}).`}
        </Typography>
      )}
    </Box>
  );
}
