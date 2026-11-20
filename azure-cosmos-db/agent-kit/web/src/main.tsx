import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { CssBaseline, ThemeProvider, createTheme } from '@mui/material';
import App from './App';

const theme = createTheme({
  colorSchemes: { light: true, dark: true },
  palette: { primary: { main: '#3b5bdb' } },
  shape: { borderRadius: 12 },
  typography: { fontFamily: 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif' },
  components: {
    MuiButton: { defaultProps: { disableElevation: true }, styleOverrides: { root: { textTransform: 'none' } } },
    MuiCard: { defaultProps: { variant: 'outlined' } },
  },
});

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ThemeProvider theme={theme} defaultMode="system">
      <CssBaseline />
      <App />
    </ThemeProvider>
  </StrictMode>,
);
