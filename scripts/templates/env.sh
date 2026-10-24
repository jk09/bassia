# Puts the default Bassia version on PATH. Sourced from the shell profiles by bassia-versions.ps1 or install.sh.
BASSIA_HOME=__ROOT__
export BASSIA_HOME
case ":$PATH:" in
	*":$BASSIA_HOME/current:"*) ;;
	*) PATH="$BASSIA_HOME/current:$PATH"; export PATH ;;
esac
