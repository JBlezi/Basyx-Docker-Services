#!/bin/sh

# wait-for.sh

HOST=$1
PORT=$2
shift 2
CMD="$@"

until nc -z $HOST $PORT; do
  echo "Waiting for $HOST:$PORT..."
  sleep 2
done

exec $CMD
